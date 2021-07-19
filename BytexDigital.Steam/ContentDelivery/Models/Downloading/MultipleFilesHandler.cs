using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Structs;

using Nito.AsyncEx;

using SteamKit2;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using static SteamKit2.DepotManifest;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class MultipleFilesHandler : IDownloadHandler
    {
        public Manifest Manifest { get; }
        public AppId AppId { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }

        public event EventHandler<ManifestFile> FileDownloaded;
        public event EventHandler<FileVerifiedArgs> FileVerified;
        public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        public event EventHandler<EventArgs> DownloadComplete;

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


        internal readonly SteamContentClient _steamContentClient;
        internal byte[] _depotKey = null;
        internal int _eventHandlersRunning = 0;
        internal AsyncAutoResetEvent _eventHandlerCompleted = new AsyncAutoResetEvent(true);

        private readonly ConcurrentDictionary<ManifestFile, FileTarget> _fileTargets = new ConcurrentDictionary<ManifestFile, FileTarget>();
        private readonly ConcurrentBag<FileWriter> _fileWriters = new ConcurrentBag<FileWriter>();
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
            if (IsRunning || _wasUsed) throw new InvalidOperationException("Download task was already started or cannot be reused.");

            try
            {
                IsRunning = true;
                _wasUsed = true;

                // Create cancellation token source
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _steamContentClient.CancellationToken);

                _serverPool = new SteamCdnServerPool(_steamContentClient, AppId, _cancellationTokenSource.Token);

                var chunks = new ConcurrentBag<ChunkJob>();

                // Filter out files that are directories or that the caller does not want
                var filteredFiles = Manifest.Files
                    .Where(file => file.Flags != Enumerations.ManifestFileFlag.Directory && condition.Invoke(file))
                    .OrderBy(x => x.FileName);

                // Verify all files in parallel
                var verificationTaskFactories = filteredFiles.Select(file => new Func<Task>(async () => await Task.Run(() => VerifyFileAsync(file, directory, chunks))));

                await ParallelAsync(
                    _steamContentClient.MaxConcurrentDownloadsPerTask,
                    new Queue<Func<Task>>(verificationTaskFactories),
                    (factory, task) => throw task.Exception,
                    _cancellationTokenSource.Token);

                if (VerificationCompleted != null)
                {
                    // Wait for all file verified eventhandlers to finish so we don't send events out of order
                    await WaitForEventHandlersAsync(_cancellationTokenSource.Token);

                    RunEventHandler(() => VerificationCompleted.Invoke(this, new VerificationCompletedArgs(chunks.Select(x => x.ManifestFile).Distinct().ToList())));
                }

                // Get the depot key with which we will process all chunks downloaded
                _depotKey = await _steamContentClient.GetDepotKeyAsync(DepotId, AppId);

                // Download all chunks in parallel
                var sortedChunks = chunks
                    .GroupBy(x => x.ManifestFile.FileName)
                    .OrderBy(x => x.Key)
                    .SelectMany(x => x);

                var taskFactoriesQueue = new Queue<Func<Task>>(sortedChunks.Select(chunkJob => new Func<Task>(async () => await Task.Run(() => DownloadChunkAsync(chunkJob, cancellationToken)))));
                var tasksFactoryFailuresLookup = new ConcurrentDictionary<Func<Task>, int>();

                await ParallelAsync(
                    _steamContentClient.MaxConcurrentDownloadsPerTask,
                    taskFactoriesQueue,
                    (factory, task) =>
                    {
                        tasksFactoryFailuresLookup.AddOrUpdate(factory, 0, (key, existingVal) => existingVal + 1);

                        if (tasksFactoryFailuresLookup.GetValueOrDefault(factory, 0) >= 10)
                        {
                            throw new SteamDownloadException(task.Exception);
                        }

                        taskFactoriesQueue.Enqueue(factory);
                    },
                    _cancellationTokenSource.Token);



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
                await Task.WhenAll(_fileWriters.Select(x => Task.Run(async () => await x.DisposeAsync().AsTask())));
                _serverPool?.Close();

                IsRunning = false;
            }
        }

        private async Task WaitForEventHandlersAsync(CancellationToken cancellationToken = default)
        {
            while (_eventHandlersRunning > 0)
            {
                await _eventHandlerCompleted.WaitAsync(cancellationToken);
            }
        }

        private async Task ParallelAsync(int maxParallel, Queue<Func<Task>> taskFactoriesQueue, Action<Func<Task>, Task> faultedTaskAction, CancellationToken cancellationToken)
        {
            var tasksRunning = new List<Task>(maxParallel);
            var tasksFactoryLookup = new Dictionary<Task, Func<Task>>();

            do
            {
                // If we were cancelled, we should wait for all tasks to finish
                if (cancellationToken.IsCancellationRequested && tasksRunning.Count > 0)
                {
                    await Task.WhenAll(tasksRunning);

                    cancellationToken.ThrowIfCancellationRequested();
                }

                while (tasksRunning.Count < maxParallel && taskFactoriesQueue.Count > 0)
                {
                    var taskFactory = taskFactoriesQueue.Dequeue();
                    var task = taskFactory();

                    tasksFactoryLookup.Add(task, taskFactory);
                    tasksRunning.Add(task);
                }

                if (tasksRunning.Count == 0) continue;

                Task completedTask = await Task.WhenAny(tasksRunning).ConfigureAwait(false);

                if (completedTask.IsFaulted)
                {
                    var taskFactory = tasksFactoryLookup[completedTask];

                    faultedTaskAction?.Invoke(taskFactory, completedTask);
                }

                tasksRunning.Remove(completedTask);
            } while (taskFactoriesQueue.Count > 0);

            if (tasksRunning.Count > 0) await Task.WhenAll(tasksRunning);
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

            var fileStream = new FileStream(filePath, FileMode.CreateNew, access: FileAccess.Write, share: FileShare.None, bufferSize: 131072, useAsync: true);
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
            var server = await _serverPool.GetServerAsync(cancellationToken).ConfigureAwait(false);
            var token = await _serverPool.AuthenticateWithServerAsync(DepotId, server).ConfigureAwait(false);

            CDNClient.DepotChunk chunkData = default;

            try
            {
                chunkData = await _serverPool.CdnClient.DownloadDepotChunkAsync(
                                DepotId,
                                chunkJob.InternalChunk,
                                server,
                                token,
                                _depotKey,
                                proxyServer: _serverPool.DesignatedProxyServer).ConfigureAwait(false);

                if (chunkData.Data.Length != chunkJob.Chunk.CompressedLength && chunkData.Data.Length != chunkJob.Chunk.UncompressedLength)
                {
                    throw new InvalidDataException("Chunk data was not the expected length.");
                }

                _serverPool.ReturnServer(server, isFaulty: false);
            }
            catch
            {
                _serverPool.ReturnServer(server, isFaulty: true);
                throw;
            }

            await chunkJob.FileWriter.WriteAsync(chunkData.ChunkInfo.Offset, chunkData.Data);

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

            // Dispose the server pool
            try { _serverPool?.Dispose(); } catch { }
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
