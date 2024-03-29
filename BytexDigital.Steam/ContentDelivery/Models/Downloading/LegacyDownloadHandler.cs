﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class LegacyDownloadHandler : IDownloadHandler
    {
        public string FileUrl { get; }
        public string FileName { get; }

        public LegacyDownloadHandler(string fileUrl, string fileName)
        {
            FileUrl = fileUrl;
            FileName = fileName;
        }

        public bool IsRunning { get; private set; }
        public double TotalProgress { get; private set; }
        public int TotalFileCount => 1;
        public ulong TotalFileSize { get; private set; }

        public event EventHandler<ManifestFile> FileDownloaded;
        public event EventHandler<FileVerifiedArgs> FileVerified;
        public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
        public event EventHandler<EventArgs> DownloadComplete;

        public Task DownloadToFolderAsync(
            string directory,
            Func<ManifestFile, bool> condition,
            CancellationToken cancellationToken = default)
        {
            return DownloadToFolderAsync(directory, cancellationToken);
        }

        public async Task DownloadToFolderAsync(string directory, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Download task was already started.");
            }

            IsRunning = true;

            var webClient = new WebClient();

            Directory.CreateDirectory(directory);

            webClient.DownloadProgressChanged += WebClient_DownloadProgressChanged;
            webClient.DownloadFileCompleted += WebClient_DownloadFileCompleted;

            await webClient.DownloadFileTaskAsync(new Uri(FileUrl), Path.Combine(directory, FileName));

            IsRunning = false;

            if (DownloadComplete != null)
            {
                _ = Task.Run(() => DownloadComplete.Invoke(this, new EventArgs()));
            }
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(Task.CompletedTask);
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
        }

        private void WebClient_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            TotalProgress = 1;
            IsRunning = false;

            if (FileDownloaded != null)
            {
                _ = Task.Run(
                    () => FileDownloaded.Invoke(
                        this,
                        new ManifestFile(
                            FileName,
                            new List<ManifestFileChunkHeader>(),
                            default,
                            TotalFileSize,
                            new byte[0])));
            }
        }

        private void WebClient_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            TotalFileSize = (ulong) e.TotalBytesToReceive;
            TotalProgress = (double) e.ProgressPercentage / 100;
        }
    }
}