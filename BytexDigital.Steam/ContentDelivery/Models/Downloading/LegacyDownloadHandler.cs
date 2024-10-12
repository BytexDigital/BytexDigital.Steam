using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Enumerations;
using BytexDigital.Steam.ContentDelivery.Exceptions;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading;

public class LegacyDownloadHandler : IDownloadHandler
{
    public string FileUrl { get; }
    public string FileName { get; }
    public string DownloadDirectory { get; protected set; }
    public Func<ManifestFile, bool> FileCondition { get; protected set; }
    public DownloadHandlerStateEnum State { get; protected set; }
    public IReadOnlyList<ManifestFile> Files { get; protected set; }

    public LegacyDownloadHandler(string fileUrl, string fileName)
    {
        FileUrl = fileUrl;
        FileName = fileName;
        State = DownloadHandlerStateEnum.Created;
    }

    public double TotalProgress { get; private set; }
    public int TotalFileCount => 1;
    public ulong TotalFileSize { get; private set; }

    public event EventHandler<ManifestFile> FileDownloaded;
    public event EventHandler<FileVerifiedArgs> FileVerified;
    public event EventHandler<VerificationCompletedArgs> VerificationCompleted;
    public event EventHandler<EventArgs> DownloadComplete;

    public Task SetupAsync(
        string directory,
        Func<ManifestFile, bool> condition,
        CancellationToken cancellationToken = default)
    {
        // Allow setup when not setup yet or already setup, but no state past it
        if (State != DownloadHandlerStateEnum.Created && State != DownloadHandlerStateEnum.SetUp &&
            State != DownloadHandlerStateEnum.Verified)
        {
            throw new DownloadHandlerStateMismatchException(DownloadHandlerStateEnum.Created, State);
        }

        DownloadDirectory = directory;
        FileCondition = condition;

        Files = new List<ManifestFile>
        {
            new(
                FileName,
                new List<ManifestFileChunkHeader>(),
                default,
                TotalFileSize,
                new byte[0])
        };

        State = DownloadHandlerStateEnum.Verified;

        return Task.CompletedTask;
    }

    public Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (State != DownloadHandlerStateEnum.Verified)
            {
                throw new DownloadHandlerStateMismatchException(DownloadHandlerStateEnum.Verified, State);
            }

            State = DownloadHandlerStateEnum.Downloading;

            var webClient = new WebClient();

            Directory.CreateDirectory(DownloadDirectory);

            webClient.DownloadProgressChanged += WebClient_DownloadProgressChanged;
            webClient.DownloadFileCompleted += WebClient_DownloadFileCompleted;

            await webClient.DownloadFileTaskAsync(new Uri(FileUrl), Path.Combine(DownloadDirectory, FileName));

            State = DownloadHandlerStateEnum.Downloaded;

            if (DownloadComplete != null)
            {
                _ = Task.Run(() => DownloadComplete.Invoke(this, new EventArgs()));
            }
        }
        catch
        {
            // In this handler, it's not a total failure if an exception is raised here, so the handler can be reused.
            State = DownloadHandlerStateEnum.SetUp;
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
        State = DownloadHandlerStateEnum.Downloaded;

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