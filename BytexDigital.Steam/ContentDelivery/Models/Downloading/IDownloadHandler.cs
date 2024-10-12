using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public interface IDownloadHandler : IAsyncDisposable, IDisposable
    {
        double TotalProgress { get; }
        int TotalFileCount { get; }
        ulong TotalFileSize { get; }
        string DownloadDirectory { get; }
        Func<ManifestFile, bool> FileCondition { get; }
        DownloadHandlerStateEnum State { get; }
        IReadOnlyList<ManifestFile> Files { get; }

        event EventHandler<FileVerifiedArgs> FileVerified;
        event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        event EventHandler<ManifestFile> FileDownloaded;
        event EventHandler<EventArgs> DownloadComplete;

        Task SetupAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default);
        Task VerifyAsync(CancellationToken cancellationToken = default); 
        Task DownloadAsync(CancellationToken cancellationToken = default);
    }
}
