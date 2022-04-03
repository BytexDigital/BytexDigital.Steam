using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;
using Nito.AsyncEx;
using SteamKit2.CDN;
using SteamKit2.Internal;
using SteamKit = SteamKit2;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamContentClient : IAsyncDisposable
    {
        // Cache
        private readonly ConcurrentDictionary<AppId, ulong> _appAccessTokens =
            new ConcurrentDictionary<AppId, ulong>();

        // Cache
        private readonly ConcurrentDictionary<uint, SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>
            _appProductInfos =
                new ConcurrentDictionary<uint, SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>();

        private readonly CancellationTokenSource _cancellationTokenSource;

        // Cache
        private readonly ConcurrentDictionary<DepotId, byte[]> _depotKeys = new ConcurrentDictionary<DepotId, byte[]>();

        // Cache
        private readonly ConcurrentDictionary<ManifestId, (DateTimeOffset, ulong)> _manifestRequestCodes =
            new ConcurrentDictionary<ManifestId, (DateTimeOffset, ulong)>();

        // Cache
        private readonly ConcurrentDictionary<uint, ulong> _packageAccessTokens =
            new ConcurrentDictionary<uint, ulong>();

        // Cache
        private readonly ConcurrentDictionary<uint, SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>
            _packageProductInfos =
                new ConcurrentDictionary<uint, SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>();

        // Shared server pools
        private readonly ConcurrentDictionary<AppId, SteamCdnServerPool> _serverPools =
            new ConcurrentDictionary<AppId, SteamCdnServerPool>();

        private readonly AsyncLock _serverPoolsLock = new AsyncLock();

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        public SteamClient SteamClient { get; }

        internal SteamKit.SteamUnifiedMessages.UnifiedService<IPublishedFile> PublishedFileService { get; }
        internal SteamKit.SteamUser SteamUser { get; }
        internal SteamKit.SteamApps SteamApps { get; }
        internal SteamKit.SteamUnifiedMessages SteamUnifiedMessagesService { get; }
        internal ConcurrentDictionary<string, SteamKit.SteamApps.CDNAuthTokenCallback> CdnAuthenticationTokens { get; }
        internal int MaxConcurrentDownloadsPerTask { get; }

        public SteamContentClient(SteamClient steamClient, int maxConcurrentDownloadsPerTask = 10)
        {
            SteamClient = steamClient;
            SteamUnifiedMessagesService = SteamClient.InternalClient.GetHandler<SteamKit.SteamUnifiedMessages>();
            PublishedFileService = SteamUnifiedMessagesService.CreateService<IPublishedFile>();
            CdnAuthenticationTokens = new ConcurrentDictionary<string, SteamKit.SteamApps.CDNAuthTokenCallback>();
            MaxConcurrentDownloadsPerTask = maxConcurrentDownloadsPerTask;
            SteamApps = SteamClient.InternalClient.GetHandler<SteamKit.SteamApps>();
            SteamUser = SteamClient.InternalClient.GetHandler<SteamKit.SteamUser>();

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(SteamClient.CancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var pool in _serverPools)
            {
                try
                {
                    pool.Value.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            return default;
        }

        public async Task<Manifest> GetManifestAsync(
            AppId appId,
            DepotId depotId,
            ManifestId manifestId,
            CancellationToken cancellationToken = default)
        {
            await SteamClient.AwaitReadyAsync(cancellationToken);

            var pool = await GetSteamCdnServerPoolAsync(appId, cancellationToken);

            if (manifestId == default)
            {
                throw new SteamManifestDownloadException($"Manifest with id = {manifestId} cannot be downloaded.");
            }

            var attempts = 0;
            const int maxAttempts = 5;

            Exception lastException = default;

            while (attempts < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Server server = default;

                try
                {
                    server = pool.GetServer(cancellationToken);

                    var depotKey = await GetDepotKeyAsync(depotId, appId);

                    //// Get manifest request code from cache or fetch it and put it into the cache
                    //ulong manifestCode;

                    //if (!_manifestRequestCodes.TryGetValue(manifestId., )

                    var manifestCode =
                        await SteamClient._steamContentHandler.GetManifestRequestCode(depotId, appId, manifestId);

                    var manifest =
                        await pool.CdnClient.DownloadManifestAsync(depotId, manifestId, manifestCode, server, depotKey);


                    if (manifest.FilenamesEncrypted)
                    {
                        manifest.DecryptFilenames(depotKey);
                    }

                    return manifest;
                }
                catch (SteamAccessDeniedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
                finally
                {
                    if (server != null)
                    {
                        try
                        {
                            pool.ReturnServer(server, true);
                        }
                        catch
                        {
                            // Ignore for retry logic
                        }
                    }

                    attempts += 1;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            throw new SteamManifestDownloadException(
                $"Could not download the manifest = {manifestId} after {maxAttempts} attempts.",
                lastException);
        }

        private async Task<SteamCdnServerPool> GetSteamCdnServerPoolAsync(
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            using var lk = await _serverPoolsLock.LockAsync(cancellationToken);

            if (_serverPools.TryGetValue(appId, out var serverPool))
            {
                return serverPool;
            }

            serverPool = new SteamCdnServerPool(this, appId);

            _serverPools.TryAdd(appId, serverPool);

            return serverPool;
        }

        public async Task<byte[]> GetDepotKeyAsync(DepotId depotId, AppId appId)
        {
            if (_depotKeys.ContainsKey(depotId))
            {
                return _depotKeys[depotId];
            }

            var result = await SteamApps.GetDepotDecryptionKey(depotId, appId);

            if (result.Result != SteamKit.EResult.OK)
            {
                if (result.Result == SteamKit.EResult.AccessDenied)
                {
                    throw new SteamDepotAccessDeniedException(depotId);
                }

                throw new SteamDepotKeyNotRetrievableException(depotId, result.Result);
            }

            while (!_depotKeys.TryAdd(depotId, result.DepotKey))
            {
            }

            return result.DepotKey;
        }

        public async Task<PublishedFileDetails> GetPublishedFileDetailsAsync(
            PublishedFileId publishedFileId,
            CancellationToken cancellationToken = default)
        {
            var data = (await GetPublishedFileDetailsAsync(new[] { publishedFileId }, cancellationToken))
                .FirstOrDefault();

            if (data == null)
            {
                throw new SteamPublishedFileDetailsFetchException();
            }

            return data;
        }

        public async Task<IReadOnlyList<PublishedFileDetails>> GetPublishedFileDetailsAsync(
            IEnumerable<PublishedFileId> publishedFileIds,
            CancellationToken cancellationToken = default)
        {
            var request = new CPublishedFile_GetDetails_Request();

            publishedFileIds.ToList().ForEach(x => request.publishedfileids.Add(x));

            var result = await PublishedFileService.SendMessage(api => api.GetDetails(request))
                .ToTask()
                .WaitAsync(cancellationToken);

            if (result.Result != SteamKit.EResult.OK)
            {
                throw new SteamPublishedFileDetailsFetchException(result.Result);
            }

            var details = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>()
                .publishedfiledetails;

            return details;
        }

        public async Task<IList<PublishedFileDetails>> GetPublishedFilesForAppIdRawAsync(
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            var paginationCursor = "*";
            var items = new List<PublishedFileDetails>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var requestDetails = new CPublishedFile_QueryFiles_Request
                {
                    query_type = (uint) SteamKit.EPublishedFileQueryType.RankedByPublicationDate,
                    cursor = paginationCursor,
                    creator_appid = appId,
                    appid = appId,
                    numperpage = 100,

                    return_vote_data = true,
                    return_children = true,
                    return_for_sale_data = true,
                    return_kv_tags = true,
                    return_metadata = true,
                    return_tags = true,
                    return_previews = true,
                    return_details = true,
                    return_short_description = true
                };

                var methodResponse = await PublishedFileService.SendMessage(api => api.QueryFiles(requestDetails))
                    .ToTask()
                    .WaitAsync(cancellationToken);

                var returnedItems = methodResponse.GetDeserializedResponse<CPublishedFile_QueryFiles_Response>();

                items.AddRange(returnedItems.publishedfiledetails);
                paginationCursor = returnedItems.next_cursor;

                if (returnedItems.publishedfiledetails.Count == 0)
                {
                    break;
                }
            }

            return items;
        }

        public async Task<IDownloadHandler> GetPublishedFileDataAsync(
            PublishedFileId publishedFileId,
            ManifestId? manifestId = default,
            CancellationToken cancellationToken = default)
        {
            var publishedFileDetails = await GetPublishedFileDetailsAsync(publishedFileId, cancellationToken);

            if (publishedFileDetails.consumer_appid == 0)
            {
                throw new SteamPublishedFileNotFoundException(publishedFileId);
            }

            if (publishedFileDetails.file_type != (uint) SteamKit.EWorkshopFileType.Community)
            {
                throw new SteamPublishedFileIsNotDownloadableException(
                    publishedFileId,
                    (SteamKit.EWorkshopFileType) publishedFileDetails.file_type);
            }

            if (!string.IsNullOrEmpty(publishedFileDetails.file_url))
            {
                return new LegacyDownloadHandler(publishedFileDetails.file_url, publishedFileDetails.filename);
            }

            // Request access if necessary
            if (!await GetHasAccessAsync(publishedFileDetails.consumer_appid, cancellationToken))
            {
                var gotFreeLicense = await GetFreeLicenseAsync(publishedFileDetails.consumer_appid, cancellationToken);

                if (!gotFreeLicense)
                {
                    throw new SteamRequiredLicenseNotFoundException(publishedFileDetails.consumer_appid);
                }
            }

            manifestId ??= publishedFileDetails.hcontent_file;

            if (!manifestId.HasValue || manifestId.Value == 0)
            {
                throw new SteamAccessDeniedException(
                    $"Cannot download workshop item because no manifest id was found (Maybe you don't have access to this workshop).");
            }

            return await GetAppDataInternalAsync(
                publishedFileDetails.consumer_appid,
                publishedFileDetails.consumer_appid,
                manifestId.Value,
                true,
                cancellationToken);
        }

        public async Task<IDownloadHandler> GetAppDataAsync(
            AppId appId,
            string branch = "public",
            string branchPassword = default,
            bool skipInaccessibleDepots = true,
            Func<Depot, bool> depotIdCondition = default,
            CancellationToken cancellationToken = default)
        {
            // Get all depots that are part of the branch
            var depots = await GetDepotsAsync(appId, branch, true, cancellationToken);

            // If the user specified a more detailed list of depots to download, filter for them
            depotIdCondition ??= x => true;
            depots = depots.Where(x => depotIdCondition.Invoke(x)).ToList();

            if (depots == null)
            {
                throw new SteamDownloadCriteriaHaveNoResultException(
                    $"No depot to download found that matches the criteria.");
            }

            var multipleFilesHandlers = new List<DefaultDownloadHandler>();

            // For each depot, create an individual download handler that will download that depot
            foreach (var depot in depots)
            {
                if (!await GetHasAccessAsync(depot.Id, cancellationToken))
                {
                    continue;
                }

                try
                {
                    if (depot.Manifests.Any())
                    {
                        var manifest = await GetManifestAsync(
                            appId,
                            depot.Id,
                            depot.Manifests.First().ManifestId,
                            cancellationToken);

                        multipleFilesHandlers.Add(new DefaultDownloadHandler(this, manifest, appId, depot.Id));
                    }

                    if (depot.EncryptedManifests.Any())
                    {
                        if (branchPassword == null)
                        {
                            throw new SteamInvalidBranchPasswordException(appId, depot.Id, branch, branchPassword);
                        }


                        var decryptedManifestMeta = await DecryptDepotManifestAsync(
                            depot.EncryptedManifests.First(),
                            branchPassword);

                        var manifest = await GetManifestAsync(
                            appId,
                            depot.Id,
                            decryptedManifestMeta.ManifestId,
                            cancellationToken);

                        multipleFilesHandlers.Add(new DefaultDownloadHandler(this, manifest, appId, depot.Id));
                    }
                }
                catch (SteamAccessDeniedException)
                {
                    if (skipInaccessibleDepots)
                    {
                        continue;
                    }

                    throw;
                }
            }

            return new MultiDepotDownloadHandler(this, multipleFilesHandlers);
        }

        public async Task<IDownloadHandler> GetAppDataAsync(
            AppId appId,
            DepotId depotId,
            string branch = "public",
            string branchPassword = default,
            bool skipInaccessibleDepots = true,
            CancellationToken cancellationToken = default)
        {
            return await GetAppDataAsync(
                appId,
                branch,
                branchPassword,
                skipInaccessibleDepots,
                x => x.Id == depotId,
                cancellationToken);
        }

        public async Task<IDownloadHandler> GetAppDataAsync(
            AppId appId,
            DepotId depotId,
            ManifestId manifestId,
            CancellationToken cancellationToken = default)
        {
            return await GetAppDataInternalAsync(
                appId,
                depotId,
                manifestId,
                false,
                cancellationToken);
        }

        public async Task<IReadOnlyList<Depot>> GetDepotsAsync(
            AppId appId,
            string branch = default,
            bool resolveSharedDepots = true,
            CancellationToken cancellationToken = default)
        {
            if (branch != null)
            {
                // Check if the branch exists.
                if ((await GetBranchesAsync(appId, cancellationToken)).All(
                        b => b.Name.ToLowerInvariant() != branch.ToLowerInvariant()))
                {
                    throw new SteamBranchNotFoundException(appId, branch);
                }
            }

            var appInfo = await GetAppInfoAsync(appId, cancellationToken);
            var allDepotData = appInfo.KeyValues.Children.First(x => x.Name == "depots");
            var filteredDepotData = new List<SteamKit2.KeyValue>();
            var parsedDepots = new List<Depot>();

            foreach (var depotData in allDepotData.Children)
            {
                if (!uint.TryParse(depotData.Name, out var depotId))
                {
                    // This is not a depot but instead meta information.
                    // Maybe we'll be interested in it at a later point in development.
                    continue;
                }

                var isSharedInstall = depotData["sharedinstall"].AsBoolean();
                AppId? depotFromApp = depotData["depotfromapp"].AsUnsignedInteger();

                if (depotFromApp.Value.Id == default)
                {
                    depotFromApp = null;
                }

                // If this is a shared install, fetch the depot data separately, because this "link" to the actual depot
                // will not contain any manifests!
                if (isSharedInstall && resolveSharedDepots)
                {
                    var sharedAppInfo = await GetAppInfoAsync(depotFromApp!.Value, cancellationToken);
                    var sharedAppInfoDepots = sharedAppInfo.KeyValues.Children.First(x => x.Name == "depots");

                    foreach (var sharedAppInfoDepot in sharedAppInfoDepots.Children)
                    {
                        if (uint.TryParse(sharedAppInfoDepot.Name, out var sharedAppInfoDepotId) &&
                            sharedAppInfoDepotId == depotId)
                        {
                            filteredDepotData.Add(sharedAppInfoDepot);
                        }
                    }
                }
                else
                {
                    filteredDepotData.Add(depotData);
                }
            }

            foreach (var depot in filteredDepotData)
            {
                var depotId = uint.Parse(depot.Name!);

                // Collect data
                var name = depot["name"].Value;
                var maxSize = depot["maxsize"].AsUnsignedLong();
                var isSharedInstall = depot["sharedinstall"].AsBoolean();
                var depotFromApp = (AppId?) depot["depotfromapp"].AsUnsignedInteger();

                var operatingSystems = new List<SteamOs>();
                var processorArchitectures = new List<string>();
                var languages = new List<string>();
                var configEntries = new Dictionary<string, string>();

                foreach (var configEntry in depot["config"].Children)
                {
                    if (configEntry.Name == "oslist")
                    {
                        operatingSystems.AddRange(
                            configEntry
                                .AsString()
                                .Split(',')
                                .Select(identifier => new SteamOs(identifier)));
                    }
                    else if (configEntry.Name == "osarch")
                    {
                        processorArchitectures.AddRange(
                            configEntry
                                .AsString()
                                .Split(','));
                    }
                    else if (configEntry.Name == "language")
                    {
                        languages.AddRange(
                            configEntry
                                .AsString()
                                .Split(','));
                    }
                    else
                    {
                        configEntries.Add(configEntry.Name, configEntry.Value);
                    }
                }

                var manifests = depot["manifests"]
                    .Children
                    .Select(x => new DepotManifest(x.Name, x.AsUnsignedLong()))
                    .ToList();

                var encryptedManifests = depot["encryptedmanifests"]
                    .Children
                    .Select(
                        manifest =>
                        {
                            if (manifest["encrypted_gid_2"] != SteamKit.KeyValue.Invalid)
                            {
                                return new DepotEncryptedManifest(
                                    appId,
                                    depotId,
                                    manifest.Name,
                                    manifest["encrypted_gid_2"].Value,
                                    DepotEncryptedManifest.EncryptionVersion.V2);
                            }

                            return new DepotEncryptedManifest(
                                appId,
                                depotId,
                                manifest.Name,
                                manifest["encrypted_gid"].Value,
                                DepotEncryptedManifest.EncryptionVersion.V1);
                        })
                    .ToList();


                // Optionally filter for a branch
                if (branch != default)
                {
                    manifests = manifests
                        .Where(x => x.BranchName.ToLowerInvariant() == branch.ToLowerInvariant())
                        .ToList();

                    encryptedManifests = encryptedManifests
                        .Where(x => x.BranchName.ToLowerInvariant() == branch.ToLowerInvariant())
                        .ToList();
                }

                // If this depot has no manifests, skip it
                if (encryptedManifests.Count == 0 && manifests.Count == 0)
                {
                    continue;
                }

                //// Filter out depots that do not match the given operating system
                //if (os != default && operatingSystems.All(x => x.Identifier != os.Identifier))
                //{
                //    continue;
                //}

                parsedDepots.Add(
                    new Depot(
                        depotId,
                        name,
                        maxSize,
                        operatingSystems,
                        processorArchitectures,
                        languages,
                        manifests,
                        encryptedManifests,
                        configEntries,
                        isSharedInstall,
                        depotFromApp));
            }

            return parsedDepots;
        }

        public async Task<Depot> GetDepotAsync(
            AppId appId,
            DepotId depotId,
            CancellationToken cancellationToken = default)
        {
            var depot = (await GetDepotsAsync(appId, cancellationToken: cancellationToken))
                .FirstOrDefault(x => x.Id == depotId);

            if (depot == null)
            {
                throw new SteamDepotNotFoundException(depotId);
            }

            return depot;
        }

        public async Task ValidateBranchPasswordAsync(IEnumerable<Depot> depots, string branch, string branchPassword)
        {
            await ValidateBranchPasswordAsync(
                depots.SelectMany(
                    x => x.EncryptedManifests.Where(
                        m => m.BranchName.ToLowerInvariant() == branch.ToLowerInvariant())),
                branchPassword);
        }

        public async Task ValidateBranchPasswordAsync(
            IEnumerable<DepotEncryptedManifest> manifests,
            string branchPassword)
        {
            foreach (var manifest in manifests)
            {
                if (branchPassword == null)
                {
                    throw new ArgumentNullException(nameof(branchPassword));
                }

                await DecryptDepotManifestAsync(manifest, branchPassword);
            }
        }

        public async Task<IReadOnlyList<Branch>> GetBranchesAsync(
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            var appInfo = await GetAppInfoAsync(appId, cancellationToken);
            var depots = appInfo.KeyValues.Children.First(x => x.Name == "depots");
            var branches = depots["branches"];

            var parsedBranches = new List<Branch>();

            foreach (var branch in branches.Children)
            {
                var name = branch.Name;
                var buildid = branch["buildid"].AsUnsignedLong();
                var description = branch["description"].AsString();
                var requiresPassword = branch["pwdrequired"].AsBoolean();
                var timeUpdated = branch["timeupdated"].AsLong();

                parsedBranches.Add(
                    new Branch(
                        name,
                        buildid,
                        description,
                        requiresPassword,
                        DateTimeOffset.FromUnixTimeSeconds(timeUpdated)));
            }

            return parsedBranches;
        }

        public async Task<DepotManifest> DecryptDepotManifestAsync(
            DepotEncryptedManifest manifest,
            string branchPassword)
        {
            if (manifest.Version == DepotEncryptedManifest.EncryptionVersion.V1)
            {
                var manifestCryptoInput = DecodeHexString(manifest.EncryptedManifestId);
                var manifestIdBytes =
                    SteamKit.CryptoHelper.VerifyAndDecryptPassword(manifestCryptoInput, branchPassword);

                if (manifestIdBytes == null)
                {
                    throw new SteamInvalidBranchPasswordException(
                        manifest.AppId,
                        manifest.DepotId,
                        manifest.BranchName,
                        branchPassword);
                }

                return new DepotManifest(manifest.BranchName, BitConverter.ToUInt64(manifestIdBytes));
            }
            else
            {
                var result = await SteamApps.CheckAppBetaPassword(manifest.AppId, branchPassword);

                if (result.BetaPasswords.All(x => x.Key != manifest.BranchName))
                {
                    throw new SteamInvalidBranchPasswordException(
                        manifest.AppId,
                        manifest.DepotId,
                        manifest.BranchName,
                        branchPassword);
                }

                var manifestCryptoInput = DecodeHexString(manifest.EncryptedManifestId);

                try
                {
                    var manifestIdBytes = SteamKit.CryptoHelper.SymmetricDecryptECB(
                        manifestCryptoInput,
                        result.BetaPasswords[manifest.BranchName]);

                    return new DepotManifest(manifest.BranchName, BitConverter.ToUInt64(manifestIdBytes));
                }
                catch (Exception ex)
                {
                    throw new SteamInvalidBranchPasswordException(
                        manifest.AppId,
                        manifest.DepotId,
                        manifest.BranchName,
                        branchPassword,
                        ex);
                }
            }
        }

        public async Task<ManifestId> GetDepotManifestIdAsync(
            AppId appId,
            DepotId depotId,
            string branch = "public",
            string branchPassword = default,
            CancellationToken cancellationToken = default)
        {
            // Enforce a default branch.
            // We can only return a manifest id if we know what branch to return for.
            branch ??= "public";

            var depots = await GetDepotsAsync(appId, branch, false, cancellationToken);
            var depot = depots.FirstOrDefault(x => x.Id == depotId);

            if (depot == null)
            {
                throw new SteamDownloadCriteriaHaveNoResultException(
                    $"No manifest could be found that matches all specified criteria.");
            }

            if (depot.IsSharedInstall)
            {
                // Rerun the method with the different app id
                return await GetDepotManifestIdAsync(
                    depot.DepotOfAppId!.Value,
                    depotId,
                    branch,
                    branchPassword,
                    cancellationToken);
            }

            var manifest = depot.Manifests.FirstOrDefault();
            var encryptedManifest = depot.EncryptedManifests.FirstOrDefault();

            if (manifest != null)
            {
                return manifest.ManifestId;
            }

            if (encryptedManifest != null)
            {
                if (branchPassword == null)
                {
                    throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword);
                }

                manifest = await DecryptDepotManifestAsync(encryptedManifest, branchPassword);

                return manifest.ManifestId;
            }

            throw new ArgumentException(
                $"Combination of app id = {appId}, depot id = {depotId} and branch = {branch} could not be found.");
        }

        internal async Task<IDownloadHandler> GetAppDataInternalAsync(
            AppId appId,
            DepotId depotId,
            ManifestId manifestId,
            bool isUserGeneratedContent = false,
            CancellationToken cancellationToken = default)
        {
            if (!await GetHasAccessAsync(appId, cancellationToken))
            {
                var gotFreeLicense = await GetFreeLicenseAsync(appId, cancellationToken);

                if (!gotFreeLicense)
                {
                    throw new SteamRequiredLicenseNotFoundException(appId);
                }
            }

            if (isUserGeneratedContent)
            {
                var appInfo = await GetAppInfoAsync(appId, cancellationToken);
                var depotsRaw = appInfo.KeyValues.Children.First(x => x.Name == "depots");

                depotId = depotsRaw["workshopdepot"].AsUnsignedInteger();

                return new DefaultDownloadHandler(
                    this,
                    await GetManifestAsync(
                        appId,
                        depotId,
                        manifestId,
                        cancellationToken),
                    appId,
                    depotId);
            }

            var depots = await GetDepotsAsync(appId, cancellationToken: cancellationToken);
            var depot = depots.FirstOrDefault(x => x.Id == depotId);

            if (depot == null)
            {
                throw new SteamDepotNotFoundException(depotId);
            }

            return new DefaultDownloadHandler(
                this,
                await GetManifestAsync(
                    appId,
                    depotId,
                    manifestId,
                    cancellationToken),
                appId,
                depotId);
        }

        internal async Task RequestAppAccessTokenIfNecessaryAsync(
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            if (_appAccessTokens.ContainsKey(appId))
            {
                return;
            }

            var accessTokenRequestResult =
                await SteamApps.PICSGetAccessTokens(new List<uint> { appId }, new List<uint>())
                    .ToTask()
                    .WaitAsync(cancellationToken);

            if (accessTokenRequestResult.AppTokensDenied.Contains(appId))
            {
                throw new SteamAppAccessTokenDeniedException(appId);
            }

            _appAccessTokens.AddOrUpdate(
                appId,
                accessTokenRequestResult.AppTokens[appId],
                (appId, val) => accessTokenRequestResult.AppTokens[appId]);
        }

        internal async Task RequestPackageAccessTokenIfNecessaryAsync(
            uint packageId,
            CancellationToken cancellationToken = default)
        {
            if (_packageAccessTokens.ContainsKey(packageId))
            {
                return;
            }

            var accessTokenRequestResult =
                await SteamApps.PICSGetAccessTokens(new List<uint>(), new List<uint> { packageId })
                    .ToTask()
                    .WaitAsync(cancellationToken);

            if (accessTokenRequestResult.PackageTokensDenied.Contains(packageId))
            {
                throw new SteamAccessDeniedException($"Access denied to package id = {packageId}.");
            }

            _packageAccessTokens.AddOrUpdate(
                packageId,
                accessTokenRequestResult.PackageTokens[packageId],
                (packageId, val) => accessTokenRequestResult.PackageTokens[packageId]);
        }

        internal async Task<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            var result = await GetAppProductInfoAsync(new List<AppId> { appId }, false, cancellationToken);

            return result.First(x => x.ID == appId);
        }

        internal async Task<List<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>> GetAppProductInfoAsync(
            List<AppId> appIds,
            bool forceUpdate = true,
            CancellationToken cancellationToken = default)
        {
            var productInfos = new List<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            var appRequests = new List<SteamKit.SteamApps.PICSRequest>();

            foreach (var appId in appIds)
            {
                // Retrieve from cache if possible
                if (!forceUpdate && _appProductInfos.TryGetValue(appId, out var info))
                {
                    productInfos.Add(info);
                    continue;
                }

                // Fetch live

                await RequestAppAccessTokenIfNecessaryAsync(appId, cancellationToken);

                appRequests.Add(
                    new SteamKit.SteamApps.PICSRequest
                    {
                        ID = appId,
                        AccessToken = _appAccessTokens[appId]
                    });
            }

            var result = await SteamApps.PICSGetProductInfo(appRequests, new List<SteamKit.SteamApps.PICSRequest>())
                .ToTask()
                .WaitAsync(cancellationToken);

            foreach (var (key, value) in result.Results!.First().Apps)
            {
                _appProductInfos.AddOrUpdate(key, value, (ek, ev) => value);
                productInfos.Add(value);
            }

            return productInfos;
        }

        internal async Task<List<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>>
            GetPackageProductInfoAsync(
                List<uint> packageIds,
                bool forceUpdate = false,
                CancellationToken cancellationToken = default)
        {
            var productInfos = new List<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            var packageRequests = new List<SteamKit.SteamApps.PICSRequest>();

            foreach (var packageId in packageIds)
            {
                // Retrieve from cache if possible
                if (!forceUpdate && _packageProductInfos.TryGetValue(packageId, out var info))
                {
                    productInfos.Add(info);
                    continue;
                }

                // Fetch live
                await RequestPackageAccessTokenIfNecessaryAsync(packageId, cancellationToken);

                packageRequests.Add(
                    new SteamKit.SteamApps.PICSRequest
                    {
                        ID = packageId,
                        AccessToken = _packageAccessTokens[packageId]
                    });
            }

            var result = await SteamApps.PICSGetProductInfo(new List<SteamKit.SteamApps.PICSRequest>(), packageRequests)
                .ToTask()
                .WaitAsync(cancellationToken);

            foreach (var (key, value) in result.Results!.First().Packages)
            {
                _packageProductInfos.AddOrUpdate(key, value, (ek, ev) => value);
                productInfos.Add(value);
            }

            return productInfos;
        }

        /// <summary>
        /// Checks whether the signed in user has access to the given ID. Can be either an app ID or a depot ID.
        /// </summary>
        /// <param name="id">App ID or depot ID.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<bool> GetHasAccessAsync(
            uint id,
            CancellationToken cancellationToken = default)
        {
            List<uint> packageIds = null;

            if (SteamUser.SteamID!.AccountType == SteamKit.EAccountType.AnonUser)
            {
                // 17906 is the package id for downloading anonymous dedicated servers.
                // One package id grants access to many app ids.
                packageIds = new List<uint> { 17906 };
            }
            else
            {
                packageIds = SteamClient.Licenses.Select(x => x.PackageID).ToList();
            }

            var packageInfos = await GetPackageProductInfoAsync(packageIds, false, cancellationToken);

            foreach (var packageId in packageIds)
            {
                var query = packageInfos.Where(x => x.ID == packageId).ToList();

                if (!query.Any())
                {
                    continue;
                }

                var packageInfo = query.First();

                if (packageInfo.KeyValues["appids"].Children.Any(x => x.AsUnsignedInteger() == id))
                {
                    return true;
                }

                if (packageInfo.KeyValues["depotids"].Children.Any(x => x.AsUnsignedInteger() == id))
                {
                    return true;
                }
            }

            return false;
        }

        internal async Task<bool> GetFreeLicenseAsync(AppId appId, CancellationToken cancellationToken = default)
        {
            var result = await SteamApps.RequestFreeLicense(appId).ToTask().WaitAsync(cancellationToken);

            return result.GrantedApps.Contains(appId);
        }

        private static byte[] DecodeHexString(string hex)
        {
            if (hex == null)
            {
                return null;
            }

            var chars = hex.Length;
            var bytes = new byte[chars / 2];

            for (var i = 0; i < chars; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            return bytes;
        }
    }
}