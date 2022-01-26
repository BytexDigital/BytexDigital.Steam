using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.Core;

using System;
using System.Threading.Tasks;

namespace BytexDigital.Steam.TestClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            //if (args.Length < 2)
            //{
            //    Console.WriteLine("Expected two arguments: username password");
            //    return;
            //}

            SteamCredentials steamCredentials = SteamCredentials.Anonymous;

            if (args.Length == 2)
            {
                steamCredentials = new SteamCredentials(args[0], args[1]);
            }
            else
            {
                steamCredentials = new SteamCredentials(args[0], args[1], args[2]);
            }

            SteamClient steamClient = new SteamClient(steamCredentials, new AuthCodeProvider(), new DirectorySteamAuthenticationFilesProvider(".\\sentries"));
            SteamContentClient steamContentClient = new SteamContentClient(steamClient, 50);

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
                //var depots = await steamContentClient.GetDepotsAsync(107410);
                //var publicDepots = await steamContentClient.GetDepotsOfBranchAsync(107410, "public");

                //await using var downloadHandler = await steamContentClient.GetAppDataAsync(107410, 228990, null, "public", null, SteamOs.Windows);
                //var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(2683654050);
                var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(497660133);

                Console.WriteLine("Starting download");

                downloadHandler.FileVerified += (sender, args) => Console.WriteLine($"Verified file {args.ManifestFile.FileName}");
                downloadHandler.FileDownloaded += (sender, args) => Console.WriteLine($"{downloadHandler.TotalProgress * 100:00.00}% {args.FileName}");
                downloadHandler.VerificationCompleted += (sender, args) => Console.WriteLine($"Verification completed, {args.QueuedFiles.Count} files queued for download");
                downloadHandler.DownloadComplete += (sender, args) => Console.WriteLine("Download completed");

                await downloadHandler.DownloadToFolderAsync(@".\download"/*, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token*/);

                await downloadHandler.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Download failed: {ex.Message}");
            }

            steamClient.Shutdown();
            Console.WriteLine("Done");

            Console.ReadLine();

            while (true)
            {
                //steamClient = null;
                Console.ReadLine();
                System.GC.Collect();
            }
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
