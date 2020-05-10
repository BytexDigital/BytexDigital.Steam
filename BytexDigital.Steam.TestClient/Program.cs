using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;

using System;
using System.Threading.Tasks;

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

            try
            {
                await steamClient.ConnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not connect to Steam: {ex.Message}");
            }

            Console.WriteLine("Connected");

            var pubFile = await steamContentClient.GetPublishedFileDetailsAsync(1765453539);
            var manifest = await steamContentClient.GetManifestAsync(221100, 221100, pubFile.hcontent_file);

            try
            {
                var downloadHandler = await steamContentClient.GetAppDataAsync(107410, 228990, null, "public", null, SteamOs.Windows);
                //var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(1765453539);

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
