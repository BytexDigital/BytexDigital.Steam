using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Exceptions;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class MultiDepotDownloadHandler : IDownloadHandler
    {
        private readonly List<IDownloadHandler> _multipleFilesHandlers;

        public MultiDepotDownloadHandler(List<IDownloadHandler> multipleFilesHandlers)
        {
            _multipleFilesHandlers = multipleFilesHandlers;

            foreach (var handler in _multipleFilesHandlers)
            {
                handler.FileVerified += (sender, args) => FileVerified?.Invoke(sender, args);
                handler.FileDownloaded += (sender, args) => FileDownloaded?.Invoke(sender, args);
            }
        }
        
        public string DownloadDirectory { get; protected set; }
        public Func<ManifestFile, bool> FileCondition { get; protected set; }
        public DownloadHandlerStateEnum State { get; protected set; }

        public double TotalProgress => _multipleFilesHandlers.Sum(x => x.TotalProgress) / _multipleFilesHandlers.Count;
        public int TotalFileCount => _multipleFilesHandlers.Sum(x => x.TotalFileCount);
        public ulong TotalFileSize => _multipleFilesHandlers.Select(x => x.TotalFileSize).Aggregate((a, b) => a + b);

        public event EventHandler<FileVerifiedArgs> FileVerified;
        public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        public event EventHandler<ManifestFile> FileDownloaded;
        public event EventHandler<EventArgs> DownloadComplete;
        
        public async Task SetupAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default)
        {
            if (State != DownloadHandlerStateEnum.Created)
            {
                throw new DownloadHandlerStateMismatchException(DownloadHandlerStateEnum.Created, State);
            }

            DownloadDirectory = directory;
            FileCondition = condition;

            foreach (var handler in _multipleFilesHandlers)
            {
                await handler.SetupAsync(directory, condition, cancellationToken);
            }
            
            State = DownloadHandlerStateEnum.SetUp;
        }

        public async Task VerifyAsync(CancellationToken cancellationToken = default)
        {
            if (State != DownloadHandlerStateEnum.SetUp)
            {
                throw new DownloadHandlerStateMismatchException(DownloadHandlerStateEnum.SetUp, State);
            }
            
            try
            {
                var filesToDownload = new List<ManifestFile>();

                foreach (var handler in _multipleFilesHandlers)
                {
                    handler.FileVerified += (sender, args) =>
                    {
                        if (args.RequiresDownload)
                        {
                            filesToDownload.Add(args.ManifestFile);
                        }
                    };
                
                    await handler.VerifyAsync(cancellationToken);
                }

                State = DownloadHandlerStateEnum.Verified;
            
                VerificationCompleted?.Invoke(this, new VerificationCompletedArgs(filesToDownload));
            }
            catch
            {
                State = DownloadHandlerStateEnum.Failed;
            }
        }

        public async Task DownloadAsync(CancellationToken cancellationToken = default)
        {
            if (State != DownloadHandlerStateEnum.Verified)
            {
                throw new DownloadHandlerStateMismatchException(DownloadHandlerStateEnum.Verified, State);
            }
            
            try
            {
                foreach (var handler in _multipleFilesHandlers)
                {
                    // Start download
                    await handler.DownloadAsync(cancellationToken);
                }

                DownloadComplete?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                State = DownloadHandlerStateEnum.Failed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var handler in _multipleFilesHandlers)
            {
                try
                {
                    await handler.DisposeAsync();
                }
                catch
                {
                    // ignore..
                }
            }
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }
    }
}