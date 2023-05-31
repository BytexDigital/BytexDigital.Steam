using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Regional;
using BytexDigital.Steam.Core.Structs;
using SteamKit2;
using SteamKit2.Discovery;
using SteamClient = BytexDigital.Steam.Core.SteamClient;

namespace BytexDigital.Steam.TestClient;

public static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Expected two arguments: username password");
            return;
        }

        var steamCredentials = SteamCredentials.Anonymous;

        steamCredentials = args.Length == 2
            ? new SteamCredentials(args[0], args[1])
            : new SteamCredentials(args[0], args[1], args[2]);

        var steamClient = new SteamClient(
            steamCredentials,
            new ConsoleSteamAuthenticator(steamCredentials.Username, ".\\auth"),
            builder => builder.WithCellID(148));

        // steamClient.ForcedServer = ServerRecord.CreateServer(
        //     "cm4-cu-sha1.cm.wmsjsteam.com", 27022,
        //     ProtocolTypes.WebSocket);
        
        var steamContentClient = new SteamContentClient(steamClient, 25);

        steamClient.InternalClientAttemptingConnect += () => Console.WriteLine("Event: Attempting connect..");
        steamClient.InternalClientConnected += () => Console.WriteLine("Event: Connected");
        steamClient.InternalClientDisconnected += () => Console.WriteLine("Event: Disconnected");
        steamClient.InternalClientLoggedOn += () => Console.WriteLine("Event: Logged on");
        steamClient.InternalClientLoggedOff += () => Console.WriteLine("Event: Logged off");

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

            //await using var downloadHandler =
            //    await steamContentClient.GetAppDataAsync(107410, 228983, 8124929965194586177);

            //var branches = await steamContentClient.GetBranchesAsync(1280290);

            //var b = await steamContentClient.GetHasAccessAsync(249504);

            //var manifestId = await steamContentClient.GetDepotManifestIdAsync(107410, 107422);
            //var depot = await steamContentClient.GetDepotAsync(107410, 1042220);

            //var depots = await steamContentClient.GetDepotsAsync(107410, "public", true);

            //await using var handler1 = await steamContentClient
            //    .GetAppDataAsync(107410, 249503, "public");

            SteamOs steamOs = new("win");
            ManifestId manifestId;

            manifestId = await steamContentClient.GetDepotManifestIdAsync(233780, 233781, "public");
            var downloadHandler = await steamContentClient.GetAppDataAsync(233780, 233781, manifestId);
            
            // await using var downloadHandler = await steamContentClient
            //     .GetAppDataAsync(
            //         233780,
            //         "public",
            //         depotIdCondition: depot => depot.Id == 233781 || depot.Id == 233782);

            //await using var downloadHandler =
            //    await steamContentClient.GetAppDataAsync(
            //        233780,
            //        "creatordlc",
            //        null,
            //        true);


            //await using var downloadHandler =
            //    await steamContentClient.GetAppDataAsync(
            //        107410,
            //        "profiling",
            //        "CautionSpecialProfilingAndTestingBranchArma3",
            //        true);

            //var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(2683654050);
            //var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(497660133);
            //var downloadHandler = await steamContentClient.GetPublishedFileDataAsync(2785679828);

            // var result = await steamContentClient.GetPublishedFileDetailsAsync(
            //     new PublishedFileId[] { 2683654050, 497660133, 2785679828 });

            Console.WriteLine("Starting download");

            downloadHandler.FileVerified += (sender, args) =>
                Console.WriteLine($"Verified file {args.ManifestFile.FileName}");
            downloadHandler.FileDownloaded += (sender, args) =>
                Console.WriteLine($"{downloadHandler.TotalProgress * 100:00.00}% {args.FileName}");
            downloadHandler.VerificationCompleted += (sender, args) =>
                Console.WriteLine($"Verification completed, {args.QueuedFiles.Count} files queued for download");
            downloadHandler.DownloadComplete += (sender, args) => Console.WriteLine("Download completed");

            await downloadHandler.DownloadToFolderAsync(
                @".\download" /*, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token*/);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Download failed: {ex.Message}");
        }

        steamClient.Shutdown();
        Console.WriteLine("Done");

        Console.ReadLine();
    }
}