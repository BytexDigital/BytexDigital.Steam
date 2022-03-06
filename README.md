![Nuget](https://img.shields.io/nuget/vpre/BytexDigital.Steam.svg?style=flat-square)

# BytexDigital.Steam

This library is a wrapper for the popular SteamKit2 library. It's primary goal is to simplify specific APIs of it to
make them easier to use.

As of now, this library's main purpose is to allow for simple content downloading (apps and workshop items) off Steam.

> :warning: **Please note that this library is in early beta. APIs may change heavily between versions.**

## Download

[nuget package](https://www.nuget.org/packages/BytexDigital.Steam/)

### Simple item download usage example

```csharp
namespace BytexDigital.Steam.TestClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Expected two arguments: username password");
                return;
            }

            SteamClient steamClient = new SteamClient(new SteamCredentials(args[0], args[1]));
            SteamContentClient steamContentClient = new SteamContentClient(steamClient);

            await steamClient.ConnectAsync();

            Console.WriteLine("Connected");

            try
            {
                var downloadHandler = await steamContentClient.GetAppDataAsync(107410, 228990, null, "public", null, SteamOs.Windows);

                Console.WriteLine("Starting download");
                var downloadTask = downloadHandler.DownloadToFolderAsync(@".\download");

                while (!downloadTask.IsCompleted)
                {
                    var delayTask = Task.Delay(100);
                    var t = await Task.WhenAny(delayTask, downloadTask);

                    Console.WriteLine($"Progress {(downloadHandler.TotalProgress * 100).ToString("00.00")}%");
                }

                await downloadTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Download failed: {ex.Message}");
            }

            steamClient.Shutdown();
            Console.WriteLine("Done");
        }
    }
}
```

#### Filter which files to download

You can use a condition to limit your download to specific files.

```csharp
var downloadTask = downloadHandler.DownloadToFolderAsync(@".\downloads", x => x.FileName.EndsWith(".exe"));
```
