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
            if (args.Length < 2)
            {
                Console.WriteLine("Expected two arguments: username password");
                return;
            }

            SteamCredentials steamCredentials = null;

            if (args.Length == 2)
            {
                steamCredentials = new SteamCredentials(args[0], args[1]);
            }
            else
            {
                steamCredentials = new SteamCredentials(args[0], args[1], args[2]);
            }

            SteamClient steamClient = new SteamClient(steamCredentials, new AuthCodeProvider(), new DirectorySteamAuthenticationFilesProvider(".\\sentries"));
            SteamContentClient steamContentClient = new SteamContentClient(steamClient);

            try
            {
                await steamClient.ConnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not connect to Steam: {ex.Message}");
                return;
            }

            Console.WriteLine("Connected");

            try
            {
                //var downloadHandler = await steamContentClient.GetAppDataAsync(107410, 228990, null, "public", null, SteamOs.Windows);
                var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(2105680247);

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

        private class AuthCodeProvider : SteamAuthenticationCodesProvider
        {
            public override string GetEmailAuthenticationCode(SteamCredentials steamCredentials)
            {
                Console.Write("Please enter your email auth code: ");

                string input = Console.ReadLine();

                Console.Write("Retrying... ");

                return input;
            }

            public override string GetTwoFactorAuthenticationCode(SteamCredentials steamCredentials)
            {
                Console.Write("Please enter your 2FA code: ");

                string input = Console.ReadLine();

                Console.Write("Retrying... ");

                return input;
            }
        }
    }
}
