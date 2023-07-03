using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Enumerations;
using BytexDigital.Steam.Core.Structs;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using SteamKit2.CDN;
using static SteamKit2.DepotManifest;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class DefaultDownloadHandler : IDownloadHandler, IAsyncDisposable, IDisposable
    {
        private CancellationTokenSource _cancellationTokenSource;
        internal byte[] _depotKey;
        internal AsyncAutoResetEvent _eventHandlerCompleted = new AsyncAutoResetEvent(true);
        internal int _eventHandlersRunning;

        private ConcurrentDictionary<ManifestFile, FileTarget> _fileTargets =
            new ConcurrentDictionary<ManifestFile, FileTarget>();

        private ConcurrentBag<FileWriter> _fileWriters = new ConcurrentBag<FileWriter>();
        private SteamCdnServerPool _serverPool;


        internal SteamContentClient _steamContentClient;
        private bool _wasUsed;

        public Manifest Manifest { get; }
        public AppId AppId { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }

        public ILogger Logger { get; set; }

        public DefaultDownloadHandler(
            SteamContentClient steamContentClient,
            Manifest manifest,
            AppId appId,
            DepotId depotId)
        {
            _steamContentClient = steamContentClient;

            Manifest = manifest;
            AppId = appId;
            DepotId = depotId;
            ManifestId = manifest.ManifestId;
        }

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
                var totalBytes = _fileTargets.Sum(x => (long) x.Value.TotalBytes);
                var currentBytes = _fileTargets.Sum(x => (long) x.Value.WrittenBytes);

                return totalBytes > 0 ? (double) currentBytes / totalBytes : 1;
            }
        }

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
        {
            await DownloadToFolderAsync(directory, x => true, cancellationToken);
        }

        public async Task DownloadToFolderAsync(
            string directory,
            Func<ManifestFile, bool> condition,
            CancellationToken cancellationToken = default)
        {
            await DownloadAsync(directory, condition, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            // Dispose all file writers to release potential file streams
            await Task.WhenAll(_fileWriters.Select(x => Task.Run(async () => await x.DisposeAsync().AsTask())));

            _fileTargets = null;
            _fileWriters = null;
            _steamContentClient = null;

            // Dispose the server pool
            try
            {
                _serverPool?.Dispose();
            }
            catch
            {
                // ignored
            }

            _serverPool = null;
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }

        public async Task DownloadAsync(
            string directory,
            Func<ManifestFile, bool> condition,
            CancellationToken cancellationToken = default)
        {
            if (IsRunning || _wasUsed)
            {
                throw new InvalidOperationException("Download task was already started and cannot be reused.");
            }

            try
            {
                IsRunning = true;
                _wasUsed = true;

                // Create cancellation token source
                _cancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _steamContentClient.CancellationToken);

                Logger?.LogTrace("Creating Steam CDN server pool");

                _serverPool = new SteamCdnServerPool(_steamContentClient, AppId, _cancellationTokenSource.Token)
                {
                    Logger = Logger
                };

                Logger?.LogTrace("Created Steam CDN server pool");

                var chunks = new ConcurrentBag<ChunkJob>();

                // Filter out files that are directories or that the caller does not want
                IEnumerable<ManifestFile> filteredFiles = Manifest.Files
                    .Where(file => file.Flags != ManifestFileFlag.Directory && condition.Invoke(file))
                    .OrderBy(x => x.FileName)
                    .ToList();

                // Filter all files that are possible duplicates
                filteredFiles = filteredFiles.GroupBy(x => x.FileName).Select(x => x.First()).ToList();
                
                if (!filteredFiles.Any())
                {
                    Logger?.LogTrace("No files to download");

                    if (DownloadComplete != null)
                    {
                        // Wait for all file verified eventhandlers to finish so we don't send events out of order
                        await WaitForEventHandlersAsync(_cancellationTokenSource.Token);

                        RunEventHandler(() => DownloadComplete!.Invoke(this, EventArgs.Empty));
                    }
                    
                    return;
                }

                // Verify all files in parallel
                var verificationTaskFactories =
                    filteredFiles.Select(
                        file => new Func<Task>(
                            async () => await Task.Run(() => VerifyFileAsync(file, directory, chunks))));

                Logger?.LogTrace("Verifying files");

                await ParallelAsync(
                    _steamContentClient.MaxConcurrentDownloadsPerTask,
                    verificationTaskFactories,
                    _cancellationTokenSource.Token);

                Logger?.LogTrace("Verification completed");

                if (VerificationCompleted != null)
                {
                    // Wait for all file verified eventhandlers to finish so we don't send events out of order
                    await WaitForEventHandlersAsync(_cancellationTokenSource.Token);

                    RunEventHandler(
                        () => VerificationCompleted!.Invoke(
                            this,
                            new VerificationCompletedArgs(chunks.Select(x => x.ManifestFile).Distinct().ToList())));
                }

                Logger?.LogTrace("Fetching depot key");

                // Get the depot key with which we will process all chunks downloaded
                _depotKey = await _steamContentClient.GetDepotKeyAsync(DepotId, AppId);

                Logger?.LogTrace("Got depot key");

                // Download all chunks in parallel
                var sortedChunks = chunks
                    .GroupBy(x => x.ManifestFile.FileName)
                    .OrderBy(x => x.Key)
                    .SelectMany(x => x);

                var taskFactoriesQueue = sortedChunks.Select(
                        chunkJob =>
                            new Func<Task>(
                                async () => await Task.Run(
                                    () => DownloadChunkAsync(chunkJob, _cancellationTokenSource.Token))))
                    .ToList();

                Logger?.LogTrace($"Starting {taskFactoriesQueue.Count} download tasks");
                
                if (taskFactoriesQueue.Count > 0)
                {
                    var tasksFactoryFailuresLookup = new ConcurrentDictionary<Func<Task>, int>();

                    await ParallelAsync(
                        _steamContentClient.MaxConcurrentDownloadsPerTask,
                        taskFactoriesQueue,
                        _cancellationTokenSource.Token);
                }
                
                Logger?.LogTrace("Completed download tasks");

                if (DownloadComplete != null)
                {
                    // Wait for all file verified eventhandlers to finish so we don't send events out of order
                    await WaitForEventHandlersAsync(_cancellationTokenSource.Token);

                    RunEventHandler(() => DownloadComplete!.Invoke(this, EventArgs.Empty));
                }
            }
            finally
            {
                if (_fileWriters.Count > 0) {
                    Logger?.LogTrace("Disposing all file writers");

                    var disposeTasks = _fileWriters.Select(x => Task.Run(async () => await x.DisposeAsync().AsTask()));

                    var cancellationTcs = new TaskCompletionSource<object>();
                    _cancellationTokenSource.Token.Register(() => cancellationTcs.TrySetCanceled(), false);

                    await Task.WhenAny(Task.WhenAll(disposeTasks), cancellationTcs.Task);
                }
                
                Logger?.LogTrace("Closing server pool");
                _serverPool?.Close();

                IsRunning = false;
            }

            Logger?.LogTrace("Exiting");
        }

        private async Task WaitForEventHandlersAsync(CancellationToken cancellationToken = default)
        {
            while (_eventHandlersRunning > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _eventHandlerCompleted.WaitAsync(cancellationToken);
            }
        }

        private async Task ParallelAsync(
            int maxParallel,
            IEnumerable<Func<Task>> taskFactories,
            CancellationToken cancellationToken)
        {
            var taskFactoriesSource = taskFactories.ToArray();
            var tasksRunning = new List<Task>(50);
            var index = 0;

            do
            {
                while (tasksRunning.Count < maxParallel && index < taskFactoriesSource.Length)
                {
                    var taskFactory = taskFactoriesSource[index++];

                    tasksRunning.Add(taskFactory());
                }

                var completedTask = await Task.WhenAny(tasksRunning).ConfigureAwait(false);

                await completedTask.ConfigureAwait(false);

                tasksRunning.Remove(completedTask);
            } while (index < taskFactoriesSource.Length || tasksRunning.Count != 0);
        }

        private Task VerifyFileAsync(ManifestFile file, string directory, ConcurrentBag<ChunkJob> chunks)
        {
            var filePath = Path.Combine(directory, file.FileName);
            var fileDirectory = Path.GetDirectoryName(filePath);

            Directory.CreateDirectory(fileDirectory);

            if (File.Exists(filePath))
            {
                using (var fs = new FileStream(filePath, FileMode.Open))
                {
                    // If the files are the same length, verify them using their hash
                    if ((ulong) fs.Length == file.TotalSize)
                    {
                        using var bs = new BufferedStream(fs);
                        using var sha1 = new SHA1Managed();

                        // If both files are identical, dont download the file
                        if (sha1.ComputeHash(bs).SequenceEqual(file.FileHash))
                        {
                            if (FileVerified != null)
                            {
                                RunEventHandler(() => FileVerified.Invoke(this, new FileVerifiedArgs(file, false)));
                            }

                            return Task.CompletedTask;
                        }
                    }
                }

                File.Delete(filePath);
            }

            var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            fileStream.SetLength((long) file.TotalSize);

            var target = new FileStreamTarget(fileStream);

            _fileTargets.TryAdd(file, target);

            target.TotalBytes = file.TotalSize;
            target.WrittenBytes = 0;

            var fileWriter = new FileWriter(this, file, target, file.TotalSize);

            _fileWriters.Add(fileWriter);

            foreach (var chunk in file.ChunkHeaders)
            {
                chunks.Add(
                    new ChunkJob
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

            if (FileVerified != null)
            {
                RunEventHandler(() => FileVerified.Invoke(this, new FileVerifiedArgs(file, true)));
            }

            return Task.CompletedTask;
        }

        private async Task DownloadChunkAsync(ChunkJob chunkJob, CancellationToken cancellationToken)
        {
            DepotChunk chunkData = default;
            Server server = default;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    server = _serverPool.GetServer(cancellationToken);

                    chunkData = await _serverPool.CdnClient.DownloadDepotChunkAsync(
                            DepotId,
                            chunkJob.InternalChunk,
                            server,
                            _depotKey,
                            _serverPool.DesignatedProxyServer)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _serverPool.ReturnServer(server, true);
                    server = null;
                }
            } while (chunkData == null);

            if (server != null)
            {
                _serverPool.ReturnServer(server, false);
            }

            Logger?.LogTrace("Writing to FileWriter");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await chunkJob.FileWriter.WriteAsync(chunkData.ChunkInfo.Offset, chunkData.Data)
                        .ConfigureAwait(false);
                    break;
                }
                catch
                {
                    // ignore
                }
            }

            Logger?.LogTrace("Completed downloading chunk");
        }

        internal void RunEventHandler(Action action)
        {
            Interlocked.Increment(ref _eventHandlersRunning);

            _ = Task.Run(
                () =>
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

        internal class FileWriter : IAsyncDisposable
        {
            private readonly AsyncSemaphore _writeLock = new AsyncSemaphore(1);
            private DefaultDownloadHandler _handler;

            public ulong WrittenBytes { get; private set; }
            public ulong ExpectedBytes { get; }
            public ManifestFile ManifestFile { get; }
            public FileTarget Target { get; }
            public bool IsDone { get; private set; }

            public FileWriter(
                DefaultDownloadHandler handler,
                ManifestFile manifestFile,
                FileTarget target,
                ulong expectedBytes)
            {
                _handler = handler;
                Target = target;
                ManifestFile = manifestFile;
                ExpectedBytes = expectedBytes;
            }

            public async ValueTask DisposeAsync()
            {
                await CancelAsync();

                _handler = null;
            }

            public async Task WriteAsync(ulong offset, byte[] data)
            {
                using var semLock = await _writeLock.LockAsync().ConfigureAwait(false);

                if (IsDone)
                {
                    return;
                }

                await Target.WriteAsync(offset, data);

                WrittenBytes += (ulong) data.Length;
                Target.WrittenBytes = WrittenBytes;

                if (WrittenBytes == ExpectedBytes)
                {
                    await Target.CompleteAsync().ConfigureAwait(false);

                    IsDone = true;

                    if (_handler.FileDownloaded != null)
                    {
                        _handler.RunEventHandler(() => _handler.FileDownloaded.Invoke(_handler, ManifestFile));
                    }
                }
            }

            public async Task CancelAsync()
            {
                try
                {
                    using var semLock = await _writeLock.LockAsync().ConfigureAwait(false);

                    await Target.CancelAsync().ConfigureAwait(false);

                    IsDone = true;
                }
                catch
                {
                    // ignored
                }
            }
        }

        internal class ChunkJob
        {
            public FileWriter FileWriter { get; set; }
            public ManifestFile ManifestFile { get; set; }
            public ManifestFileChunkHeader Chunk { get; set; }
            public ChunkData InternalChunk { get; set; }
        }
    }
}