using System;
using System.Collections.Generic;
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
        public int TotalFileCount => 1;
        public ulong TotalFileSize { get; private set; }

        public DirectFileHandler(string fileUrl, string fileName)
        {
            FileUrl = fileUrl;
            FileName = fileName;
        }

        public event EventHandler<ManifestFile> FileDownloaded;
        public event EventHandler<FileVerifiedArgs> FileVerified;
        public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        public event EventHandler<EventArgs> DownloadComplete;

        public Task DownloadToFolderAsync(string directory, Func<ManifestFile, bool> condition, CancellationToken cancellationToken = default)
            => DownloadToFolderAsync(directory, cancellationToken);

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
        {
            if (IsRunning) throw new InvalidOperationException("Download task was already started.");
            IsRunning = true;

            var webClient = new WebClient();

            Directory.CreateDirectory(directory);

            webClient.DownloadProgressChanged += WebClient_DownloadProgressChanged;
            webClient.DownloadFileCompleted += WebClient_DownloadFileCompleted;

            await webClient.DownloadFileTaskAsync(new Uri(FileUrl), Path.Combine(directory, FileName));

            IsRunning = false;

            if (DownloadComplete != null) _ = Task.Run(() => DownloadComplete.Invoke(this, new EventArgs()));
        }

        private void WebClient_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            TotalProgress = 1;
            IsRunning = false;

            if (FileDownloaded != null) _ = Task.Run(() => FileDownloaded.Invoke(this, new ManifestFile(
                FileName,
                new List<ManifestFileChunkHeader>(),
                default, TotalFileSize, new byte[0])));
        }

        private void WebClient_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            TotalFileSize = (ulong)e.TotalBytesToReceive;
            TotalProgress = (double)e.ProgressPercentage / 100;
        }
    }
}
