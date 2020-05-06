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
