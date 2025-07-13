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
        
        /// <summary>
        /// Read-only list of all files that are going to be downloaded. Only available after <see cref="SetupAsync"/>
        /// has been run atleast once.
        /// </summary>
        IReadOnlyList<ManifestFile> Files { get; }

        event EventHandler<FileVerifiedArgs> FileVerified;
        event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        event EventHandler<ManifestFile> FileDownloaded;
        event EventHandler<EventArgs> DownloadComplete;

        /// <summary>
        /// Sets up necessary information for the download handler. Can be run multiple times to update information,
        /// but not after having run <see cref="VerifyAsync"/>.
        /// </summary>
        /// <param name="directory">Directory to download to.</param>
        /// <param name="condition">Condition that determines which files the user wants to download.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns></returns>
        Task SetupAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Verifies the local files against their online counterparts and determines the final list of files to download.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task VerifyAsync(CancellationToken cancellationToken = default); 
        Task DownloadAsync(CancellationToken cancellationToken = default);
    }
}
