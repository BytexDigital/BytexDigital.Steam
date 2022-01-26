using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Structs;

using Microsoft.Extensions.Logging;

using Nito.AsyncEx;

using SteamKit2.CDN;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using static SteamKit2.DepotManifest;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class MultipleFilesHandler : IDownloadHandler, IAsyncDisposable, IDisposable
    {
        public Manifest Manifest { get; }
        public AppId AppId { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }

        public event EventHandler<ManifestFile> FileDownloaded;
        public event EventHandler<FileVerifiedArgs> FileVerified;
        public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        public event EventHandler<EventArgs> DownloadComplete;

        public ILogger Logger { get; set; }

        public bool IsRunning { get; private set; }
        public int TotalFileCount => _fileTargets.Count;
        public ulong TotalFileSize => _fileTargets.Select(x => x.Key.TotalSize).Aggregate(0UL, (a, b) => a + b);
        public double TotalProgress
        {
            get
            {
                var totalBytes = _fileTargets.Sum(x => (long)x.Value.TotalBytes);
                var currentBytes = _fileTargets.Sum(x => (long)x.Value.WrittenBytes);

                return (double)currentBytes / (totalBytes > 0 ? totalBytes : 1);
            }
        }


        internal SteamContentClient _steamContentClient;
        internal byte[] _depotKey = null;
        internal int _eventHandlersRunning = 0;
        internal AsyncAutoResetEvent _eventHandlerCompleted = new AsyncAutoResetEvent(true);

        private ConcurrentDictionary<ManifestFile, FileTarget> _fileTargets = new ConcurrentDictionary<ManifestFile, FileTarget>();
        private ConcurrentBag<FileWriter> _fileWriters = new ConcurrentBag<FileWriter>();
        private CancellationTokenSource _cancellationTokenSource;
        private SteamCdnServerPool _serverPool;
        private bool _wasUsed = false;

        public MultipleFilesHandler(SteamContentClient steamContentClient, Manifest manifest, AppId appId, DepotId depotId, ManifestId manifestId)
        {
            _steamContentClient = steamContentClient;

            Manifest = manifest;
            AppId = appId;
            DepotId = depotId;
            ManifestId = manifestId;
        }

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
            => await DownloadToFolderAsync(directory, x => true, cancellationToken);

        public async Task DownloadToFolderAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default)
            => await DownloadAsync(directory, condition, cancellationToken);

        public async Task DownloadAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default)
        {
            if (IsRunning || _wasUsed) throw new InvalidOperationException("Download task was already started and cannot be reused.");

            try
            {
                IsRunning = true;
                _wasUsed = true;

                // Create cancellation token source
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _steamContentClient.CancellationToken);

                Logger?.LogTrace($"Creating Steam CDN server pool");

                _serverPool = new SteamCdnServerPool(_steamContentClient, AppId, _cancellationTokenSource.Token)
                {
                    Logger = Logger
                };

                Logger?.LogTrace($"Created Steam CDN server pool");

                var chunks = new ConcurrentBag<ChunkJob>();

                // Filter out files that are directories or that the caller does not want
                IEnumerable<ManifestFile> filteredFiles = Manifest.Files
                    .Where(file => file.Flags != Enumerations.ManifestFileFlag.Directory && condition.Invoke(file))
                    .OrderBy(x => x.FileName);

                // Filter all files that are possible duplicates
                filteredFiles = filteredFiles.GroupBy(x => x.FileName).Select(x => x.First());

                // Verify all files in parallel
                var verificationTaskFactories = filteredFiles.Select(file => new Func<Task>(async () => await Task.Run(() => VerifyFileAsync(file, directory, chunks))));

                Logger?.LogTrace($"Verifying files");

                await ParallelAsync(
                    _steamContentClient.MaxConcurrentDownloadsPerTask,
                    verificationTaskFactories,
                    (factory, task) => throw task.Exception,
                    _cancellationTokenSource.Token);

                Logger?.LogTrace($"Verification completed");

                if (VerificationCompleted != null)
                {
                    // Wait for all file verified eventhandlers to finish so we don't send events out of order
                    await WaitForEventHandlersAsync(_cancellationTokenSource.Token);

                    RunEventHandler(() => VerificationCompleted.Invoke(this, new VerificationCompletedArgs(chunks.Select(x => x.ManifestFile).Distinct().ToList())));
                }

                Logger?.LogTrace($"Fetching depot key");

                // Get the depot key with which we will process all chunks downloaded
                _depotKey = await _steamContentClient.GetDepotKeyAsync(DepotId, AppId);

                Logger?.LogTrace($"Got depot key");

                // Download all chunks in parallel
                var sortedChunks = chunks
                    .GroupBy(x => x.ManifestFile.FileName)
                    .OrderBy(x => x.Key)
                    .SelectMany(x => x);

                var taskFactoriesQueue = sortedChunks.Select(chunkJob => new Func<Task>(async () => await Task.Run(() => DownloadChunkAsync(chunkJob, _cancellationTokenSource.Token)))).ToList();
                var tasksFactoryFailuresLookup = new ConcurrentDictionary<Func<Task>, int>();

                Logger?.LogTrace($"Starting {taskFactoriesQueue.Count} download tasks");

                await ParallelAsync(
                    _steamContentClient.MaxConcurrentDownloadsPerTask,
                    taskFactoriesQueue,
                    (factory, task) =>
                    {
                        tasksFactoryFailuresLookup.AddOrUpdate(factory, 0, (key, existingVal) => existingVal + 1);

                        Logger?.LogTrace($"Failed to download chunk at attempt {tasksFactoryFailuresLookup.GetValueOrDefault(factory, 0)}: {task.Exception.Message}");

                        if (tasksFactoryFailuresLookup.GetValueOrDefault(factory, 0) >= 10)
                        {
                            throw new SteamDownloadException(task.Exception);
                        }
                    },
                    _cancellationTokenSource.Token);

                Logger?.LogTrace($"Completed download tasks");

                if (DownloadComplete != null)
                {
                    // Wait for all file verified eventhandlers to finish so we don't send events out of order
                    await WaitForEventHandlersAsync(_cancellationTokenSource.Token);

                    RunEventHandler(() => DownloadComplete.Invoke(this, new EventArgs()));
                }
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                Logger?.LogTrace($"Disposing all file writers");

                var disposeTasks = _fileWriters.Select(x => Task.Run(async () => await x.DisposeAsync().AsTask()));

                var cancellationTCS = new TaskCompletionSource<object>();
                _cancellationTokenSource.Token.Register(() => cancellationTCS.TrySetCanceled(), useSynchronizationContext: false);

                await Task.WhenAny(Task.WhenAll(disposeTasks), cancellationTCS.Task);


                Logger?.LogTrace($"Closing server pool");
                _serverPool?.Close();

                IsRunning = false;
            }

            Logger?.LogTrace($"Exiting");
        }

        private async Task WaitForEventHandlersAsync(CancellationToken cancellationToken = default)
        {
            while (_eventHandlersRunning > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _eventHandlerCompleted.WaitAsync(cancellationToken);
            }
        }

        private async Task ParallelAsync(int maxParallel, IEnumerable<Func<Task>> taskFactories, Action<Func<Task>, Task> faultedTaskAction, CancellationToken cancellationToken)
        {
            var tasksRunning = new List<Task>();
            var tasksFactoryLookup = new Dictionary<Task, Func<Task>>();
            var tasksFactoriesQueue = new Queue<Func<Task>>(taskFactories);

            while (tasksRunning.Count > 0 || tasksFactoriesQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Logger?.LogTrace($"ParallelAsync: Checking if I can queue more tasks");
                while (tasksRunning.Count < maxParallel && tasksFactoriesQueue.Count > 0)
                {
                    var taskFactory = tasksFactoriesQueue.Dequeue();

                    Logger?.LogTrace($"ParallelAsync: Starting task");
                    var task = taskFactory();

                    // Save the taskfactory that produced this task in case the task failed and we want to reattempt it
                    if (tasksFactoryLookup.ContainsKey(task))
                    {
                        var oldTask = tasksFactoryLookup.Keys.First(x => x == task);

                        if (oldTask.IsCompleted)
                        {
                            tasksFactoryLookup.Remove(oldTask);
                        }
                    }
                    tasksFactoryLookup.Add(task, taskFactory);
                    tasksRunning.Add(task);

                    cancellationToken.ThrowIfCancellationRequested();
                }


                Logger?.LogTrace($"ParallelAsync: Waiting for any of {tasksRunning.Count} tasks to complete");
                Task completedTask = await Task.WhenAny(tasksRunning).ConfigureAwait(false);

                Logger?.LogTrace($"ParallelAsync: A task completed");

                if (completedTask.IsFaulted)
                {
                    Logger?.LogTrace($"ParallelAsync: Task was faulted");

                    // Reattempt the faulted task by putting it back into the queue at the end of it
                    var taskFactory = tasksFactoryLookup[completedTask];

                    // Do something the caller wants us to do (e.g. enforce a max policy on retries or something)
                    faultedTaskAction?.Invoke(taskFactory, completedTask);

                    // Reattempt
                    tasksFactoryLookup.Remove(completedTask);
                    tasksFactoriesQueue.Enqueue(taskFactory);
                }

                tasksRunning.Remove(completedTask);
            }
        }

        private Task VerifyFileAsync(ManifestFile file, string directory, ConcurrentBag<ChunkJob> chunks)
        {
            var filePath = Path.Combine(directory, file.FileName);
            var fileDirectory = Path.GetDirectoryName(filePath);

            Directory.CreateDirectory(fileDirectory);

            if (File.Exists(filePath))
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open))
                {

                    // If the files are the same length, verify them using their hash
                    if ((ulong)fs.Length == file.TotalSize)
                    {
                        using BufferedStream bs = new BufferedStream(fs);
                        using SHA1Managed sha1 = new SHA1Managed();

                        // If both files are identical, dont download the file
                        if (sha1.ComputeHash(bs).SequenceEqual(file.FileHash))
                        {
                            if (FileVerified != null) RunEventHandler(() => FileVerified.Invoke(this, new FileVerifiedArgs(file, false)));

                            return Task.CompletedTask;
                        }
                    }
                }

                File.Delete(filePath);
            }

            var fileStream = new FileStream(filePath, FileMode.Create, access: FileAccess.Write, share: FileShare.None, bufferSize: 131072, useAsync: true);
            fileStream.SetLength((long)file.TotalSize);

            var target = new FileStreamTarget(fileStream);

            _fileTargets.TryAdd(file, target);

            target.TotalBytes = file.TotalSize;
            target.WrittenBytes = 0;

            var fileWriter = new FileWriter(this, file, target, file.TotalSize);

            _fileWriters.Add(fileWriter);

            foreach (var chunk in file.ChunkHeaders)
            {
                chunks.Add(new ChunkJob
                {
                    ManifestFile = file,
                    Chunk = chunk,
                    FileWriter = fileWriter,
                    InternalChunk = new ChunkData
                    {
                        Checksum = chunk.Checksum,
                        ChunkID = chunk.Id,
                        CompressedLength = chunk.CompressedLength,
                        Offset = chunk.Offset,
                        UncompressedLength = chunk.UncompressedLength
                    }
                });
            }

            if (FileVerified != null) RunEventHandler(() => FileVerified.Invoke(this, new FileVerifiedArgs(file, true)));

            return Task.CompletedTask;
        }

        private async Task DownloadChunkAsync(ChunkJob chunkJob, CancellationToken cancellationToken)
        {
            var cancellationTokenWithTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token,
                cancellationToken).Token;

            Logger?.LogTrace($"Getting download server");
            var server = await _serverPool.GetServerAsync(cancellationTokenWithTimeout).ConfigureAwait(false);

            Logger?.LogTrace($"Authenticating with server if necessary");
            var token = await _serverPool.AuthenticateWithServerAsync(DepotId, server).ConfigureAwait(false);

            DepotChunk chunkData;

            try
            {
                Logger?.LogTrace($"Downloading chunk");
                var stopwatch = Stopwatch.StartNew();

                chunkData = await _serverPool.CdnClient.DownloadDepotChunkAsync(
                                DepotId,
                                chunkJob.InternalChunk,
                                server,
                                _depotKey,
                                _serverPool.DesignatedProxyServer).ConfigureAwait(false);

                if (chunkData.Data.Length != chunkJob.Chunk.CompressedLength && chunkData.Data.Length != chunkJob.Chunk.UncompressedLength)
                {
                    throw new InvalidDataException("Chunk data was not the expected length.");
                }

                stopwatch.Stop();

                _serverPool.ReturnServer(server, isFaulty: false, rating: (int)stopwatch.ElapsedMilliseconds);
            }
            catch
            {
                _serverPool.ReturnServer(server, isFaulty: true);
                throw;
            }

            Logger?.LogTrace($"Writing to FileWriter");
            await chunkJob.FileWriter.WriteAsync(chunkData.ChunkInfo.Offset, chunkData.Data);

            Logger?.LogTrace($"Completed downloading chunk");
        }

        internal void RunEventHandler(Action action)
        {
            Interlocked.Increment(ref _eventHandlersRunning);

            _ = Task.Run(() =>
            {
                try
                {
                    action.Invoke();
                }
                finally
                {
                    Interlocked.Decrement(ref _eventHandlersRunning);
                    _eventHandlerCompleted.Set();
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            // Dispose all file writers to release potential file streams
            await Task.WhenAll(_fileWriters.Select(x => Task.Run(async () => await x.DisposeAsync().AsTask())));

            _fileTargets = null;
            _fileWriters = null;
            _steamContentClient = null;

            // Dispose the server pool
            try { _serverPool?.Dispose(); } catch { }

            _serverPool = null;
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }

        internal class FileWriter : IAsyncDisposable
        {
            public ulong WrittenBytes { get; private set; }
            public ulong ExpectedBytes { get; private set; }
            public ManifestFile ManifestFile { get; }
            public FileTarget Target { get; }
            public bool IsDone { get; private set; }

            private AsyncSemaphore _writeLock = new AsyncSemaphore(1);
            private MultipleFilesHandler _handler;

            public FileWriter(MultipleFilesHandler handler, ManifestFile manifestFile, FileTarget target, ulong expectedBytes)
            {
                _handler = handler;
                Target = target;
                ManifestFile = manifestFile;
                ExpectedBytes = expectedBytes;
            }

            public async Task WriteAsync(ulong offset, byte[] data)
            {
                using (var semLock = await _writeLock.LockAsync().ConfigureAwait(false))
                {
                    if (IsDone) return;

                    await Target.WriteAsync(offset, data);

                    WrittenBytes += (ulong)data.Length;
                    Target.WrittenBytes = WrittenBytes;

                    if (WrittenBytes == ExpectedBytes)
                    {
                        await Target.CompleteAsync().ConfigureAwait(false);

                        IsDone = true;

                        if (_handler.FileDownloaded != null) _handler.RunEventHandler(() => _handler.FileDownloaded.Invoke(_handler, ManifestFile));
                    }
                }
            }

            public async Task CancelAsync()
            {
                int a = new Random().Next(0, 1000);

                try
                {
                    using (var semLock = await _writeLock.LockAsync().ConfigureAwait(false))
                    {
                        await Target.CancelAsync().ConfigureAwait(false);

                        IsDone = true;
                    }
                }
                catch
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                await CancelAsync();

                _handler = null;
            }
        }

        internal class ChunkJob
        {
            public FileWriter FileWriter { get; set; }
            public ManifestFile ManifestFile { get; set; }
            public ManifestFileChunkHeader Chunk { get; set; }
            public SteamKit2.DepotManifest.ChunkData InternalChunk { get; set; }
        }
    }
}
