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
        double BufferUsage { get; }
        Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default);
        Task DownloadToFolderAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default);
    }
}
