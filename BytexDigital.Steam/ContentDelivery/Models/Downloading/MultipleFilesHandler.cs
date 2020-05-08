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
    public class MultipleFilesHandler : IDownloadHandler
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

        internal readonly SteamContentClient _steamContentClient;
        internal readonly ConcurrentQueue<ChunkTask> _chunks = new ConcurrentQueue<ChunkTask>();
        private readonly ConcurrentDictionary<ManifestFile, FileTarget> _fileTargets = new ConcurrentDictionary<ManifestFile, FileTarget>();
        private CancellationTokenSource _cancellationTokenSource;
        internal (FileTarget, Exception)? _fileTargetException;
        private AsyncAutoResetEvent _chunkWasWrittenEvent = new AsyncAutoResetEvent(false);
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

            var workers = new List<Worker>();

            for (int i = 0; i < _steamContentClient.MaxConcurrentDownloadsPerTask; i++)
            {
                var worker = new Worker(this);

                workers.Add(worker);

                // Give each worker their own cdn client
                var cdnClient = await _steamContentClient.SteamCdnClientPool.GetClientAsync(AppId, DepotId, _steamContentClient.CancellationToken);

                worker.UseCdnClient(cdnClient);
            }

            foreach (var worker in workers)
            {
                worker.Work();
            }

            while (!workers.All(x => x.Done))
            {
                await Task.Delay(100);

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    foreach (var worker in workers)
                    {
                        worker.Stop();
                    }

                    break;
                }

                if (_fileTargetException.HasValue)
                {
                    throw new SteamDownloadTaskFileTargetWrappedException(_fileTargetException.Value.Item1, _fileTargetException.Value.Item2);
                }

                foreach (var worker in workers)
                {
                    if (worker.IsCdnClientFaulted)
                    {
                        Console.WriteLine("Replacing faulted client");

                        _steamContentClient.SteamCdnClientPool.ReturnClient(worker.CdnClient, false);

                        var newCdnClient = await _steamContentClient.SteamCdnClientPool.GetClientAsync(AppId, DepotId, _steamContentClient.CancellationToken);

                        worker.UseCdnClient(newCdnClient);
                    }
                }
            }

            foreach (var worker in workers)
            {
                _steamContentClient.SteamCdnClientPool.ReturnClient(worker.CdnClient);
            }
        }

        private class Worker
        {
            private readonly MultipleFilesHandler _filesHandler;
            private bool _stop = false;

            public CdnClient CdnClient { get; private set; }
            public bool Done { get; private set; }
            public Task Task { get; private set; }
            public bool IsCdnClientFaulted { get; private set; }


            public Worker(MultipleFilesHandler filesHandler)
            {
                _filesHandler = filesHandler;
            }

            public void UseCdnClient(CdnClient cdnClient)
            {
                CdnClient = cdnClient;

                lock (this)
                {
                    IsCdnClientFaulted = false;
                }
            }

            public void Work()
            {
                Task = Task.Factory.StartNew(async () =>
                {
                    int faultCounter = 0;

                    while (!_filesHandler._chunks.IsEmpty && !_stop)
                    {
                        _filesHandler._chunks.TryDequeue(out var chunk);

                        if (chunk == null) continue;

                        try
                        {
                            var result = await CdnClient.InternalCdnClient.DownloadDepotChunkAsync(_filesHandler.DepotId, chunk.InternalChunk, CdnClient.ServerWrapper.Server);

                            try
                            {
                                chunk.FileWriter.Write(chunk.InternalChunk.Offset, result.Data);
                            }
                            catch (Exception ex)
                            {
                                if (_filesHandler._fileTargetException == null)
                                {
                                    _filesHandler._fileTargetException = (chunk.FileWriter.Target, ex);
                                }
                            }
                        }
                        catch
                        {
                            if (++faultCounter > 5)
                            {
                                Console.WriteLine("Error downloading chunk");

                                faultCounter = 0;

                                lock (this)
                                {
                                    IsCdnClientFaulted = true;
                                }
                            }

                            _filesHandler._chunks.Enqueue(chunk);
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


        //private async Task DownloadDepotChunk(ChunkTask chunk, CdnClientMeasurementWrapper client, uint depotId, FileWriter fileWriter)
        //{
        //    try
        //    {
        //        var result = await client.DownloadDepotChunkAsync(depotId, chunk.InternalChunk).ConfigureAwait(false);

        //        try
        //        {
        //            fileWriter.Write(chunk.InternalChunk.Offset, result.Data);
        //            Console.WriteLine("Finished task");
        //        }
        //        catch (Exception ex)
        //        {
        //            if (_fileTargetException == null)
        //            {
        //                _fileTargetException = (fileWriter.Target, ex);
        //            }
        //        }
        //    }
        //    catch
        //    {
        //        _cdnClientsFaultsCount.AddOrUpdate(client, 1, (c, v) => v + 1); // Make sure the dispatcher knows this cdn client resulted in an error
        //        _chunks.Enqueue(chunk); // Requeue the chunk for later downloading
        //    }
        //}

        internal class FileWriter
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

        internal class ChunkTask
        {
            public FileWriter FileWriter { get; set; }
            public ManifestFileChunkHeader Chunk { get; set; }
            public SteamKit2.DepotManifest.ChunkData InternalChunk { get; set; }
        }
    }
}
