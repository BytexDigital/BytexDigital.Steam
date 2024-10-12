using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using SteamKit2;
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

        Console.WriteLine("Asking Steam for CM servers...");

        var servers = steamClient.InternalClient.Configuration.ServerList.GetAllEndPoints();

        Console.WriteLine(
            $"CM servers found: {string.Join(", ", servers.Select(x => $"{x.GetHost()}:{x.GetPort()}"))}");

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

        var depots = await steamContentClient.GetDepotsAsync(107410, "legacy");

        var handler = await steamContentClient.GetAppDataAsync(107410,
            "legacy",
            "Arma3Legacy216",
            true,
            x => depots.Any(d => d.Id == x.Id));
        
        try
        {
            await using var downloadHandler = new MultiDepotDownloadHandler(
                [handler]
            );

            Console.WriteLine("Starting download");

            downloadHandler.FileVerified += (sender, args) =>
                Console.WriteLine($"Verified file {args.ManifestFile.FileName}");
            downloadHandler.FileDownloaded += (sender, args) =>
                Console.WriteLine($"{downloadHandler.TotalProgress * 100:00.00}% {args.FileName}");
            downloadHandler.VerificationCompleted += (sender, args) =>
                Console.WriteLine($"Verification completed, {args.QueuedFiles.Count} files queued for download");
            downloadHandler.DownloadComplete += (sender, args) => Console.WriteLine("Download completed");

            await downloadHandler.SetupAsync(@".\download", file => true);
            await downloadHandler.VerifyAsync();
            await downloadHandler.DownloadAsync();
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