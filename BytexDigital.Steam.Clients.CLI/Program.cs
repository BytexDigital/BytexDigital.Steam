using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;
using CommandLine;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace BytexDigital.Steam.Clients.CLI
{
    class Program
    {
        public class Options
        {

            [Option("username", Required = true, HelpText = "Username to use when logging into Steam.")]
            public string Username { get; set; }

            [Option("password", Required = true, HelpText = "Password to use when logging into Steam.")]
            public string Password { get; set; }

            [Option("targetdir", HelpText = "Specifies a directory to perform an action in.")]
            public string TargetDirectory { get; set; }

            [Option("branch", HelpText = "Specifies a product branch.")]
            public string Branch { get; set; }

            [Option("branchpassword", HelpText = "Specifies a product banch password.")]
            public string BranchPassword { get; set; }

            [Option("os", HelpText = "Specifies an operating system.")]
            public string OS { get; set; }

            [Option("appid", HelpText = "Specifies an app ID to use.")]
            public uint? AppId { get; set; }

            [Option("manifestid", HelpText = "Specifies an manifest ID to use.")]
            public ulong? ManifestId { get; set; }

            [Option("workshop-download-item", Group = "Action", HelpText = "Downloads a workshop item.", Default = false)]
            public bool DownloadWorkshopItem { get; set; }

            [Option("workshopid", HelpText = "Specifies a workshop item ID to use.")]
            public uint? WorkshopFileId { get; set; }

        }

        private static SteamClient _steamClient = null;
        private static SteamContentClient _steamContentClient = null;


        static async Task Main(string[] args)
        {
            var result = await Parser.Default.ParseArguments<Options>(args)
                .WithParsedAsync(RunOptions);

            result.WithNotParsed(HandleParsingError);
        }

        static async Task RunOptions(Options opt)
        {
            _steamClient = new SteamClient(new SteamCredentials(opt.Username, opt.Password));
            _steamContentClient = new SteamContentClient(_steamClient);

            Console.Write("Connecting to Steam... ");

            try
            {
                await _steamClient.ConnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Failed! Error: {ex.Message}");
                Environment.Exit(3);
            }

            Console.WriteLine("OK.");

            if (opt.DownloadWorkshopItem)
            {
                await DownloadWorkshopItem(opt);
            }
            else
            {
                Console.WriteLine("No action to run specified, exiting.");
            }

            _steamClient.Shutdown();
        }

        static void HandleParsingError(IEnumerable<Error> errors)
        {
            Console.WriteLine("Error parsing arguments");
            Environment.Exit(1);
        }

        static async Task DownloadWorkshopItem(Options opt)
        {
            if (string.IsNullOrEmpty(opt.TargetDirectory))
            {
                Console.WriteLine("Error: Please specify a target directory.");
                Environment.Exit(101);
            }

            if (string.IsNullOrEmpty(opt.OS))
            {
                Console.WriteLine("Error: Please specify a OS.");
                Environment.Exit(101);
            }

            if (!opt.AppId.HasValue)
            {
                Console.WriteLine("Error: Please specify an app id.");
                Environment.Exit(102);
            }

            if (!opt.WorkshopFileId.HasValue)
            {
                Console.WriteLine("Error: Please specify a workshop item id.");
                Environment.Exit(102);
            }

            if (string.IsNullOrEmpty(opt.Branch))
            {
                opt.Branch = "public";

                Console.WriteLine($"Warning: No branch was specified, using default branch = {opt.Branch}");
            }

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                SteamOs steamOs = new SteamOs(opt.OS);

                Console.Write($"Attempting to start download of item {opt.WorkshopFileId.Value}... ");

                var downloadHandler = await _steamContentClient.GetPublishedFileDataAsync(
                    opt.WorkshopFileId.Value,
                    opt.ManifestId,
                    opt.Branch,
                    opt.BranchPassword, steamOs);

                var downloadTask = downloadHandler.DownloadToFolderAsync(opt.TargetDirectory);

                Console.WriteLine("OK.");
                Console.WriteLine("Downloading...");

                while (!downloadTask.IsCompleted)
                {
                    var delayTask = Task.Delay(500);
                    var t = await Task.WhenAny(delayTask, downloadTask);

                    Console.WriteLine($"Progress {(downloadHandler.TotalProgress * 100).ToString("00.00")}%");
                }

                await downloadTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}{(ex.InnerException != null ? $" Inner Exception: {ex.InnerException.Message}" : "")}");
                Environment.Exit(110);
            }

            sw.Stop();
            Console.WriteLine($"Download completed, it took {sw.Elapsed:hh\\:mm\\:ss}");
        }
    }
}
