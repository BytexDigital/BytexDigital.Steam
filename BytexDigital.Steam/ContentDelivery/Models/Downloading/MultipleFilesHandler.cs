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

using static BytexDigital.Steam.ContentDelivery.Models.Downloading.MultipleFilesHandler;
using static SteamKit2.DepotManifest;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class MultipleFilesHandler : IDownloadHandler
    {
        public Manifest Manifest { get; }
        public AppId AppId { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }

        public bool IsRunning { get; private set; }
        public double BufferUsage { get; private set; }
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
        internal readonly ConcurrentQueue<ChunkTask> _chunks = new ConcurrentQueue<ChunkTask>();
        internal readonly ConcurrentQueue<(ChunkTask, byte[])> _downloadedChunksBuffer = new ConcurrentQueue<(ChunkTask, byte[])>();
        internal readonly AsyncSemaphore _allowChunkDownloading = new AsyncSemaphore(1);
        internal byte[] _depotKey = null;
        private readonly ConcurrentDictionary<ManifestFile, FileTarget> _fileTargets = new ConcurrentDictionary<ManifestFile, FileTarget>();
        private CancellationTokenSource _cancellationTokenSource;
        private AsyncSemaphore _chunkWasWrittenEvent = new AsyncSemaphore(0);
        public const int MIN_REQUIRED_ERRORS_FOR_CLIENT_REPLACEMENT = 5;

        public MultipleFilesHandler(SteamContentClient steamContentClient, Manifest manifest, AppId appId, DepotId depotId, ManifestId manifestId)
        {
            _steamContentClient = steamContentClient;
            Manifest = manifest;
            AppId = appId;
            DepotId = depotId;
            ManifestId = manifestId;
        }

        //public async Task<double> AwaitDownloadProgressChangedAsync(CancellationToken? cancellationTokenOptional = null)
        //{
        //    var cancellationToken = cancellationTokenOptional.HasValue ? cancellationTokenOptional.Value : CancellationToken.None;

        //    if (_cancellationTokenSource != null)
        //    {
        //        cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken.Value);
        //    }


        //}

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
            => await DownloadToFolderAsync(directory, x => true, cancellationToken);

        public async Task DownloadChangesToFolderAsync(string directory, CancellationToken cancellationToken = default)
        {
            await DownloadToFolderAsync(directory, file =>
            {
                var filePath = Path.Combine(directory, file.FileName);

                if (!File.Exists(filePath)) return true;

                byte[] localHash = null;

                // Calculate local SHA1
                using (FileStream fs = new FileStream(filePath, FileMode.Open))
                using (BufferedStream bs = new BufferedStream(fs))
                {
                    using (SHA1Managed sha1 = new SHA1Managed())
                    {
                        localHash = sha1.ComputeHash(bs);
                    }
                }

                return !localHash.SequenceEqual(file.FileHash);
            }, cancellationToken);
        }

        public async Task DownloadToFolderAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default)
        {
            await DownloadAsync(x =>
            {
                if (x.Flags == Enumerations.ManifestFileFlag.Directory) return FileTarget.None;
                if (!condition.Invoke(x)) return FileTarget.None;

                var filePath = Path.Combine(directory, x.FileName);
                var fileDirectory = Path.GetDirectoryName(filePath);

                Directory.CreateDirectory(fileDirectory);

                if (File.Exists(filePath)) File.Delete(filePath);

                var fileStream = new FileStream(filePath, FileMode.CreateNew, access: FileAccess.Write, share: FileShare.None, bufferSize: 131072, useAsync: true);
                fileStream.SetLength((long)x.TotalSize);

                return new FileStreamTarget(fileStream);
            }, cancellationToken);
        }

        public async Task DownloadAsync(Func<ManifestFile, FileTarget> fileTargetGenerator, CancellationToken cancellationToken = default)
        {
            if (IsRunning) throw new InvalidOperationException("Download task was already started.");

            IsRunning = true;

            // Create cancellation token source
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _steamContentClient.CancellationToken);

            var chunkTasks = new List<Task>();
            var fileWriters = new List<FileWriter>();

            foreach (var file in Manifest.Files)
            {
                var target = fileTargetGenerator.Invoke(file);

                if (target == FileTarget.None) continue;

                _fileTargets.TryAdd(file, target);

                target.TotalBytes = file.TotalSize;
                target.WrittenBytes = 0;

                var fileWriter = new FileWriter(target, file.TotalSize);

                foreach (var chunk in file.ChunkHeaders)
                {
                    _chunks.Enqueue(new ChunkTask
                    {
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

                fileWriters.Add(fileWriter);
            }

            if (_fileTargets.Count == 0 || _cancellationTokenSource.IsCancellationRequested) return;

            // Get depot key for decryption
            _depotKey = await (await _steamContentClient.SteamCdnServerPool.GetClientAsync()).GetDepotKeyAsync(DepotId, AppId);

            var downloadWorkers = new List<ChunkDownloadWorker>();
            var writeWorkers = new List<ChunkWriterWorker>();

            for (int i = 0; i < _steamContentClient.MaxConcurrentDownloadsPerTask; i++)
            {
                var downloadWorker = new ChunkDownloadWorker(this);
                downloadWorkers.Add(downloadWorker);
            }

            for (int i = 0; i < 10; i++)
            {
                writeWorkers.Add(new ChunkWriterWorker(this));
            }

            foreach (var worker in downloadWorkers) worker.Work();
            foreach (var worker in writeWorkers) worker.Work();

            bool isDownloadingLocked = false;

            while (!downloadWorkers.All(x => x.Done) || !_downloadedChunksBuffer.IsEmpty)
            {
                await Task.Delay(100);

                if (writeWorkers.Any(x => x.Task.IsFaulted))
                {
                    var faultedTask = writeWorkers.First(x => x.Task.IsFaulted);

                    throw faultedTask.Task.Exception;
                }

                if (downloadWorkers.Any(x => x.Task.IsFaulted))
                {
                    var faultedTask = downloadWorkers.First(x => x.Task.IsFaulted);

                    throw faultedTask.Task.Exception;
                }

                ulong bytesInBuffer = _downloadedChunksBuffer
                        .Select(x => (ulong)x.Item1.InternalChunk.UncompressedLength)
                        .Aggregate(0UL, (a, b) => a + b);

                double bufferUsage = bytesInBuffer / (double)_steamContentClient.ChunkBufferSize;
                BufferUsage = bufferUsage > 1 ? 1 : bufferUsage;

                if (!isDownloadingLocked && bytesInBuffer >= _steamContentClient.ChunkBufferSize)
                {
                    await _allowChunkDownloading.LockAsync();
                    isDownloadingLocked = true;
                }
                else
                {
                    if (isDownloadingLocked && bytesInBuffer <= _steamContentClient.ChunkBufferSize * _steamContentClient.BufferUsageThreshold)
                    {
                        isDownloadingLocked = false;

                        _allowChunkDownloading.Release();
                    }
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    foreach (var worker in downloadWorkers) worker.Stop();
                    foreach (var worker in writeWorkers) worker.Stop();

                    break;
                }
            }

            // Stop all writers
            foreach (var writer in writeWorkers) writer.Stop();

            Task.WaitAll(writeWorkers.Select(x => x.Task).ToArray());
        }

        private class ChunkWriterWorker
        {
            private readonly MultipleFilesHandler _filesHandler;
            private bool _stop = false;

            public Task Task { get; private set; }
            public bool Done => Task.IsCompleted;

            public ChunkWriterWorker(MultipleFilesHandler filesHandler)
            {
                _filesHandler = filesHandler;
            }


            public void Work()
            {
                Task = Task.Factory.StartNew(async () =>
                {
                    while (!_stop || !_filesHandler._downloadedChunksBuffer.IsEmpty)
                    {
                        if (_filesHandler._downloadedChunksBuffer.IsEmpty)
                        {
                            await Task.Delay(100);
                        }

                        bool hasData = _filesHandler._downloadedChunksBuffer.TryDequeue(out (ChunkTask chunkTask, byte[] data) availableData);

                        if (!hasData) continue;

                        try
                        {
                            var depotChunk = new SteamKit2.CDNClient.DepotChunk(availableData.chunkTask.InternalChunk, availableData.data);
                            var verified = false;

                            if (_filesHandler._depotKey != null)
                            {
                                depotChunk.Process(_filesHandler._depotKey);
                                verified = true;
                            }

                            if (!verified)
                            {
                                using (SHA1Managed sha1 = new SHA1Managed())
                                {
                                    if (!depotChunk.ChunkInfo.Checksum.SequenceEqual(CryptoHelper.AdlerHash(depotChunk.Data)))
                                    {
                                        throw new InvalidOperationException("Invalid chunk data received.");
                                    }
                                }
                            }

                            await availableData.chunkTask.FileWriter.WriteAsync(availableData.chunkTask.Chunk.Offset, depotChunk.Data);
                        }
                        catch (InvalidOperationException)
                        {
                            _filesHandler._chunks.Enqueue(availableData.chunkTask);
                        }
                        catch (Exception ex)
                        {
                            throw new SteamDownloadTaskFileTargetWrappedException(availableData.chunkTask.FileWriter.Target, ex);
                        }
                    }
                }, TaskCreationOptions.LongRunning);
            }

            public void Stop()
            {
                _stop = true;
            }
        }

        private class ChunkDownloadWorker
        {
            private readonly MultipleFilesHandler _filesHandler;
            private bool _stop = false;

            public bool Done { get; private set; }
            public Task Task { get; private set; }

            public ChunkDownloadWorker(MultipleFilesHandler filesHandler)
            {
                _filesHandler = filesHandler;
            }

            public void Work()
            {
                Task = Task.Factory.StartNew(async () =>
                {
                    int faultCounter = 0;
                    SteamCdnClient cdnClient = null;

                    while (!_filesHandler._chunks.IsEmpty && !_stop)
                    {
                        if (cdnClient == null)
                        {
                            cdnClient = await _filesHandler._steamContentClient.SteamCdnServerPool.GetClientAsync();
                            await cdnClient.AuthenticateCdnClientAsync(_filesHandler.AppId, _filesHandler.DepotId);
                        }

                        _filesHandler._chunks.TryDequeue(out var chunk);

                        if (chunk == null)
                        {
                            await Task.Delay(50);
                            continue;
                        }

                        await _filesHandler._allowChunkDownloading.LockAsync();
                        _filesHandler._allowChunkDownloading.Release();

                        try
                        {
                            var result = await cdnClient.DownloadChunkAsync(_filesHandler.AppId, _filesHandler.DepotId, chunk.InternalChunk, _filesHandler._cancellationTokenSource.Token);

                            if (result.Length != chunk.Chunk.CompressedLength && result.Length != chunk.Chunk.UncompressedLength)
                            {
                                throw new InvalidDataException("Chunk data was not the expected length.");
                            }

                            _filesHandler._downloadedChunksBuffer.Enqueue((chunk, result));
                        }
                        catch (Exception ex) when (!(ex is SteamDownloadTaskFileTargetWrappedException))
                        {
                            if (faultCounter >= 5)
                            {
                                //_filesHandler._steamContentClient.SteamCdnServerPool.ReturnClient(cdnClient, false);
                                _filesHandler._steamContentClient.SteamCdnServerPool.NotifyClientError(cdnClient);
                                cdnClient = null;
                            }
                        }
                    }

                    Done = true;
                }, TaskCreationOptions.LongRunning);
            }

            public void Stop()
            {
                _stop = true;
            }
        }

        internal class FileWriter
        {
            public ulong WrittenBytes { get; private set; }
            public FileTarget Target { get; }
            public ulong ExpectedBytes { get; private set; }
            public bool IsDone => WrittenBytes == ExpectedBytes;

            private AsyncSemaphore _writeLock = new AsyncSemaphore(1);

            public FileWriter(FileTarget target, ulong expectedBytes)
            {
                Target = target;
                ExpectedBytes = expectedBytes;
            }

            public async Task WriteAsync(ulong offset, byte[] data)
            {
                await _writeLock.LockAsync();

                await Target.WriteAsync(offset, data);

                WrittenBytes += (ulong)data.Length;
                Target.WrittenBytes = WrittenBytes;

                if (WrittenBytes == ExpectedBytes)
                {
                    await Target.CompleteAsync();
                }

                _writeLock.Release();
            }
        }
    }

    internal class ChunkTask
    {
        public FileWriter FileWriter { get; set; }
        public ManifestFileChunkHeader Chunk { get; set; }
        public SteamKit2.DepotManifest.ChunkData InternalChunk { get; set; }
    }
}
