using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Structs;

using Nito.AsyncEx;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using static BytexDigital.Steam.ContentDelivery.SteamCdnClientPool;
using static SteamKit2.DepotManifest;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class FilesHandler : IDownloadHandler
    {
        public Manifest Manifest { get; }
        public AppId AppId { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }

        public bool IsRunning { get; private set; }
        public double TotalProgress
        {
            get
            {
                var totalBytes = _fileTargets.Sum(x => (long)x.Value.TotalBytes);
                var currentBytes = _fileTargets.Sum(x => (long)x.Value.WrittenBytes);

                return (double)currentBytes / (totalBytes > 0 ? totalBytes : 1);
            }
        }

        private readonly SteamContentClient _steamContentClient;
        private readonly ConcurrentQueue<ChunkTask> _chunks = new ConcurrentQueue<ChunkTask>();
        private readonly ConcurrentDictionary<ManifestFile, FileTarget> _fileTargets = new ConcurrentDictionary<ManifestFile, FileTarget>();
        private readonly RoundRobinClientPool _cdnClients = new RoundRobinClientPool();
        private readonly ConcurrentDictionary<CdnClientWrapper, int> _cdnClientsFaultsCount = new ConcurrentDictionary<CdnClientWrapper, int>();
        private CancellationTokenSource _cancellationTokenSource;
        private (FileTarget, Exception)? _fileTargetException;
        private AsyncAutoResetEvent _chunkWasWrittenEvent = new AsyncAutoResetEvent(false);
        public const int MIN_REQUIRED_ERRORS_FOR_CLIENT_REPLACEMENT = 5;
        public const int NUM_CDN_CLIENTS_UTILIZED = 5;
        public const int MAX_CONCURRENT_DOWNLOADS = 20;

        public FilesHandler(SteamContentClient steamContentClient, Manifest manifest, AppId appId, DepotId depotId, ManifestId manifestId)
        {
            _steamContentClient = steamContentClient;
            Manifest = manifest;
            AppId = appId;
            DepotId = depotId;
            ManifestId = manifestId;
        }

        public async Task DownloadToFolderAsync(string directory, CancellationToken? cancellationToken = null)
            => await DownloadToFolderAsync(directory, x => true, cancellationToken);

        public async Task DownloadToFolderAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken? cancellationToken = null)
        {
            await DownloadAsync(x =>
            {
                if (x.Flags == Enumerations.ManifestFileFlag.Directory) return FileTarget.None;
                if (!condition.Invoke(x)) return FileTarget.None;

                var filePath = Path.Combine(directory, x.FileName);
                var fileDirectory = Path.GetDirectoryName(filePath);

                Directory.CreateDirectory(fileDirectory);

                if (File.Exists(filePath)) File.Delete(filePath);

                var fileStream = new FileStream(filePath, FileMode.CreateNew);
                fileStream.SetLength((long)x.TotalSize);

                return new FileStreamTarget(fileStream);
            }, cancellationToken);
        }

        public async Task DownloadAsync(Func<ManifestFile, FileTarget> fileTargetGenerator, CancellationToken? cancellationToken = null)
        {
            if (IsRunning) throw new InvalidOperationException("Download task was already started.");

            IsRunning = true;

            // Create cancellation token source
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken ?? CancellationToken.None, _steamContentClient.CancellationToken);

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

            if (_cancellationTokenSource.IsCancellationRequested) return;

            // Acquire amount of CDN clients
            for (int i = 0; i < NUM_CDN_CLIENTS_UTILIZED; i++)
            {
                var cdnClient = await _steamContentClient.SteamCdnClientPool.GetClient(AppId, DepotId, _steamContentClient.CancellationToken);

                _cdnClients.Add(cdnClient);
                _cdnClientsFaultsCount.TryAdd(cdnClient, 0);
            }

            while (!fileWriters.All(x => x.IsDone))
            {
                if (_cancellationTokenSource.IsCancellationRequested) return;
                if (_fileTargetException != null) throw new SteamDownloadTaskFileTargetWrappedException(_fileTargetException.Value.Item1, _fileTargetException.Value.Item2);

                while (chunkTasks.Count(x => !x.IsCompleted) >= MAX_CONCURRENT_DOWNLOADS)
                {
                    await Task.WhenAny(chunkTasks);
                }

                // Replace faulty cdn clients
                foreach (var faultyCdnClient in _cdnClientsFaultsCount.Where(x => x.Value >= MIN_REQUIRED_ERRORS_FOR_CLIENT_REPLACEMENT))
                {
                    if (!_cdnClients.GetAll().Contains(faultyCdnClient.Key))
                    {
                        _cdnClientsFaultsCount.TryRemove(faultyCdnClient.Key, out var _);
                        continue;
                    }

                    _cdnClients.Remove(faultyCdnClient.Key);
                    _cdnClientsFaultsCount.TryRemove(faultyCdnClient.Key, out var _);
                    _steamContentClient.SteamCdnClientPool.ReturnClient(faultyCdnClient.Key, false);

                    var newClient = await _steamContentClient.SteamCdnClientPool.GetClient(AppId, DepotId, _cancellationTokenSource.Token);

                    _cdnClients.Add(newClient);
                    _cdnClientsFaultsCount.TryAdd(newClient, 0);
                }

                // Dequeue next chunk to work on it
                _chunks.TryDequeue(out ChunkTask chunk);

                if (chunk != null)
                {
                    var cdnClient = _cdnClients.Get();
                    var downloadTask = DownloadDepotChunk(chunk, cdnClient, DepotId, chunk.FileWriter);

                    chunkTasks.Add(downloadTask);
                }

                chunkTasks.RemoveAll(x => x.IsCompleted);
            }

            foreach (var cdnClientWrapper in _cdnClients.GetAll()) _steamContentClient.SteamCdnClientPool.ReturnClient(cdnClientWrapper);
        }

        //public async Task<double> AwaitDownloadProgressChangedAsync(CancellationToken? cancellationTokenOptional = null)
        //{
        //    var cancellationToken = cancellationTokenOptional.HasValue ? cancellationTokenOptional.Value : CancellationToken.None;

        //    if (_cancellationTokenSource != null)
        //    {
        //        cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken.Value);
        //    }


        //}

        private async Task DownloadDepotChunk(ChunkTask chunk, CdnClientWrapper client, uint depotId, FileWriter fileWriter)
        {
            try
            {
                var result = await client.CdnClient.DownloadDepotChunkAsync(depotId, chunk.InternalChunk, client.ServerWrapper.Server).ConfigureAwait(false);

                try
                {
                    fileWriter.Write(chunk.InternalChunk.Offset, result.Data);
                }
                catch (Exception ex)
                {
                    if (_fileTargetException == null)
                    {
                        _fileTargetException = (fileWriter.Target, ex);
                    }
                }
            }
            catch
            {
                _cdnClientsFaultsCount.AddOrUpdate(client, 1, (c, v) => v + 1); // Make sure the dispatcher knows this cdn client resulted in an error
                _chunks.Enqueue(chunk); // Requeue the chunk for later downloading
            }
        }

        private class FileWriter
        {
            public ulong WrittenBytes { get; private set; }
            public FileTarget Target { get; }
            public ulong ExpectedBytes { get; private set; }
            public bool IsDone => WrittenBytes == ExpectedBytes;

            public FileWriter(FileTarget target, ulong expectedBytes)
            {
                Target = target;
                ExpectedBytes = expectedBytes;
            }

            public void Write(ulong offset, byte[] data)
            {
                lock (this)
                {
                    Target.Write(offset, data);

                    WrittenBytes += (ulong)data.Length;
                    Target.WrittenBytes = WrittenBytes;

                    if (WrittenBytes == ExpectedBytes)
                    {
                        Target.Completed();
                    }
                }
            }
        }

        private class ChunkTask
        {
            public FileWriter FileWriter { get; set; }
            public ManifestFileChunkHeader Chunk { get; set; }
            public SteamKit2.DepotManifest.ChunkData InternalChunk { get; set; }
        }

        private class RoundRobinClientPool
        {
            private readonly List<CdnClientWrapper> _cdnClientWrappers = new List<CdnClientWrapper>();
            private int _selectionIndex = 0;

            public void Add(CdnClientWrapper cdnClientWrapper)
            {
                _cdnClientWrappers.Add(cdnClientWrapper);
            }

            public CdnClientWrapper Get()
            {
                var cdnClientWrapper = _cdnClientWrappers[_selectionIndex];

                _selectionIndex = ++_selectionIndex % _cdnClientWrappers.Count;

                return cdnClientWrapper;
            }

            public void Remove(CdnClientWrapper cdnClientWrapper)
            {
                _cdnClientWrappers.Remove(cdnClientWrapper);
            }

            public IReadOnlyList<CdnClientWrapper> GetAll() => _cdnClientWrappers;
        }
    }
}
