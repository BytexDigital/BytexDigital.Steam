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
            new ConsoleSteamAuthenticator(steamCredentials.Username, ".\\auth"));

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
            await using var downloadHandler = await steamContentClient
                .GetAppDataAsync(
                    233780,
                    "public",
                    depotIdCondition: depot => true /* download all depots of branch */);

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