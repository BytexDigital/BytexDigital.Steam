using System;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public interface IDownloadHandler
    {
        bool IsRunning { get; }
        double TotalProgress { get; }
        int TotalFileCount { get; }
        ulong TotalFileSize { get; }

        event EventHandler<FileVerifiedArgs> FileVerified;
        event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        event EventHandler<ManifestFile> FileDownloaded;
        event EventHandler<EventArgs> DownloadComplete;

        Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default);
        Task DownloadToFolderAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default);
    }
}
