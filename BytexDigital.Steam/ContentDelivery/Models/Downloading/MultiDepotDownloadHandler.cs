using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class MultiDepotDownloadHandler : IDownloadHandler
    {
        private readonly List<IDownloadHandler> _multipleFilesHandlers;
        private readonly SteamContentClient _steamContentClient;

        public MultiDepotDownloadHandler(
            SteamContentClient steamContentClient,
            List<IDownloadHandler> multipleFilesHandlers)
        {
            _steamContentClient = steamContentClient;
            _multipleFilesHandlers = multipleFilesHandlers;
        }

        public bool IsRunning => _multipleFilesHandlers.Any(x => x.IsRunning);
        public double TotalProgress => _multipleFilesHandlers.Sum(x => x.TotalProgress) / _multipleFilesHandlers.Count;
        public int TotalFileCount => _multipleFilesHandlers.Sum(x => x.TotalFileCount);
        public ulong TotalFileSize => _multipleFilesHandlers.Select(x => x.TotalFileSize).Aggregate((a, b) => a + b);

        public event EventHandler<FileVerifiedArgs> FileVerified;
        public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        public event EventHandler<ManifestFile> FileDownloaded;
        public event EventHandler<EventArgs> DownloadComplete;

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
        {
            await DownloadToFolderAsync(directory, x => true, cancellationToken);
        }

        public async Task DownloadToFolderAsync(
            string directory,
            Func<ManifestFile, bool> condition,
            CancellationToken cancellationToken = default)
        {
            foreach (var handler in _multipleFilesHandlers)
            {
                // Hook up all event handlers
                handler.FileVerified += (sender, args) => FileVerified?.Invoke(sender, args);
                handler.VerificationCompleted += (sender, args) => VerificationCompleted?.Invoke(sender, args);
                handler.FileDownloaded += (sender, args) => FileDownloaded?.Invoke(sender, args);

                // Start download
                await handler.DownloadToFolderAsync(directory, condition, cancellationToken);
            }

            DownloadComplete?.Invoke(this, EventArgs.Empty);
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