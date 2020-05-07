using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public interface IDownloadHandler
    {
        bool IsRunning { get; }
        double TotalProgress { get; }
        Task DownloadToFolderAsync(string directory, CancellationToken? cancellationToken = null);
    }
}
