using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class DirectFileHandler : IDownloadHandler
    {
        public bool IsRunning { get; private set; }
        public double TotalProgress { get; private set; }
        public string FileUrl { get; }
        public string FileName { get; }
        public double BufferUsage => 0;
        public int TotalFileCount => 1;
        public ulong TotalFileSize { get; private set; }

        public DirectFileHandler(string fileUrl, string fileName)
        {
            FileUrl = fileUrl;
            FileName = fileName;
        }

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
        {
            var webClient = new WebClient();

            Directory.CreateDirectory(directory);

            webClient.DownloadProgressChanged += WebClient_DownloadProgressChanged;
            webClient.DownloadFileCompleted += WebClient_DownloadFileCompleted;

            if (IsRunning) throw new InvalidOperationException("Download task was already started.");
            IsRunning = true;

            await webClient.DownloadFileTaskAsync(new Uri(FileUrl), Path.Combine(directory, FileName));
        }

        private void WebClient_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            TotalProgress = 1;
            IsRunning = false;
        }

        private void WebClient_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            TotalFileSize = (ulong)e.TotalBytesToReceive;
            TotalProgress = (double)e.ProgressPercentage / 100;
        }
    }
}
