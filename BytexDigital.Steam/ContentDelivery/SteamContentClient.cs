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
using BytexDigital.Steam.Extensions;
using SteamKit2.CDN;
using SteamKit2.Internal;
using SteamKit = SteamKit2;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamContentClient
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentDictionary<DepotId, byte[]> _depotKeys = new ConcurrentDictionary<DepotId, byte[]>();

        private readonly ConcurrentDictionary<AppId, ulong> _productAccessKeys =
            new ConcurrentDictionary<AppId, ulong>();

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

        public async Task<Manifest> GetManifestAsync(AppId appId, DepotId depotId, ManifestId manifestId,
            string branch = null, string branchPassword = null, CancellationToken cancellationToken = default)
        {
            await SteamClient.AwaitReadyAsync(cancellationToken);

            using var pool = new SteamCdnServerPool(this, appId);

            var attempts = 0;
            const int maxAttempts = 5;

            Exception lastException = default;

            while (attempts < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Server server = default;

                try
                {
                    server = await pool.GetServerAsync(cancellationToken);

                    var depotKey = await GetDepotKeyAsync(depotId, appId);
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
                catch (TaskCanceledException)
                {
                    throw;
                }
                // catch (SteamDepotAccessDeniedException)
                // {
                //     throw;
                // }
                // catch (SteamKit.SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized ||
                //                                                       ex.StatusCode == HttpStatusCode.Forbidden ||
                //                                                       ex.StatusCode == HttpStatusCode.NotFound)
                // {
                //     throw new SteamManifestDownloadException(ex);
                // }
                catch (Exception ex)
                {
                    lastException = ex;
                    // Retry..
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
                $"Could not download the manifest = {manifestId} after {maxAttempts} attempts.", lastException);
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

        public async Task<PublishedFileDetails> GetPublishedFileDetailsAsync(PublishedFileId publishedFileId)
        {
            var request = new CPublishedFile_GetDetails_Request();
            request.publishedfileids.Add(publishedFileId);

            var result = await PublishedFileService.SendMessage(api => api.GetDetails(request));

            if (result.Result == SteamKit.EResult.OK)
            {
                var details = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>().publishedfiledetails
                    .First();

                return details;
            }

            throw new SteamPublishedFileDetailsFetchException(result.Result);
        }

        public async Task<IList<PublishedFileDetails>> GetPublishedFilesForAppIdRawAsync(AppId appId,
            CancellationToken? cancellationToken = null)
        {
            cancellationToken = cancellationToken ?? CancellationToken.None;

            var paginationCursor = "*";
            var items = new List<PublishedFileDetails>();

            while (!cancellationToken.Value.IsCancellationRequested)
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

                var methodResponse = await PublishedFileService.SendMessage(api => api.QueryFiles(requestDetails));

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

        public async Task<IDownloadHandler> GetPublishedFileDataAsync(PublishedFileId publishedFileId,
            ManifestId? manifestId = null, string? branch = null, string? branchPassword = null, SteamOs? os = null)
        {
            var publishedFileDetails = await GetPublishedFileDetailsAsync(publishedFileId);

            if (publishedFileDetails.consumer_appid == 0)
            {
                throw new SteamPublishedFileNotFoundException(publishedFileId);
            }

            if (!string.IsNullOrEmpty(publishedFileDetails.file_url))
            {
                return new DirectFileHandler(publishedFileDetails.file_url, publishedFileDetails.filename);
            }

            return await GetAppDataInternalAsync(
                publishedFileDetails.consumer_appid,
                publishedFileDetails.consumer_appid,
                manifestId ?? publishedFileDetails.hcontent_file,
                branch,
                branchPassword,
                os,
                true);
        }

        public async Task<IDownloadHandler> GetAppDataAsync(AppId appId, DepotId depotId, ManifestId? manifestId = null,
            string branch = "public", string? branchPassword = null, SteamOs? os = null)
        {
            if (!manifestId.HasValue)
            {
                manifestId = await GetDepotDefaultManifestIdAsync(appId, depotId, branch, branchPassword);
            }

            return await GetAppDataInternalAsync(appId, depotId, manifestId.Value, branch, branchPassword,
                os ?? SteamClient.GetSteamOs());
        }

        public async Task<IReadOnlyList<Depot>> GetDepotsOfBranchAsync(AppId appId, string branch)
        {
            return (await GetDepotsAsync(appId))
                .Where(x => x.Manifests.Any(x => x.BranchName.ToLowerInvariant() == branch.ToLowerInvariant()))
                .ToList();
        }

        public async Task<IReadOnlyList<Depot>> GetDepotsAsync(AppId appId)
        {
            var appInfo = await GetAppInfoAsync(appId);
            var depots = appInfo.KeyValues.Children.First(x => x.Name == "depots");
            var parsedDepots = new List<Depot>();

            foreach (var depot in depots.Children)
            {
                var isDepot = uint.TryParse(depot.Name, out var depotId);

                if (!isDepot)
                {
                    continue;
                }

                var name = depot["name"].Value;
                var maxSize = depot["maxsize"].AsUnsignedLong();
                var isSharedInstall = depot["sharedinstall"].AsBoolean();
                AppId? depotFromApp = depot["depotfromapp"].AsUnsignedInteger();

                if (depotFromApp.Value.Id == default)
                {
                    depotFromApp = null;
                }

                var operatingSystems = new List<SteamOs>();
                var configEntries = new Dictionary<string, string>();

                foreach (var configEntry in depot["config"].Children)
                {
                    if (configEntry.Name == "oslist")
                    {
                        operatingSystems.AddRange(configEntry
                            .AsString()
                            .Split(',')
                            .Select(identifier => new SteamOs(identifier)));
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
                    .Select(manifest =>
                    {
                        if (manifest["encrypted_gid_2"] != SteamKit.KeyValue.Invalid)
                        {
                            return new DepotEncryptedManifest(appId, depotId, manifest.Name,
                                manifest["encrypted_gid_2"].Value, DepotEncryptedManifest.EncryptionVersion.V2);
                        }

                        return new DepotEncryptedManifest(appId, depotId, manifest.Name,
                            manifest["encrypted_gid"].Value, DepotEncryptedManifest.EncryptionVersion.V1);
                    })
                    .ToList();

                parsedDepots.Add(new Depot(depotId, name, maxSize, operatingSystems, manifests, encryptedManifests,
                    configEntries, isSharedInstall, depotFromApp));
            }

            return parsedDepots;
        }

        public async Task<IReadOnlyList<Branch>> GetBranchesAsync(AppId appId)
        {
            var appInfo = await GetAppInfoAsync(appId);
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

                parsedBranches.Add(new Branch(name, buildid, description, requiresPassword,
                    DateTimeOffset.FromUnixTimeSeconds(timeUpdated)));
            }

            return parsedBranches;
        }

        public async Task<DepotManifest> DecryptDepotManifestAsync(DepotEncryptedManifest manifest,
            string branchPassword)
        {
            if (manifest.Version == DepotEncryptedManifest.EncryptionVersion.V1)
            {
                var manifestCryptoInput = DecodeHexString(manifest.EncryptedManifestId);
                var manifestIdBytes =
                    SteamKit.CryptoHelper.VerifyAndDecryptPassword(manifestCryptoInput, branchPassword);

                if (manifestIdBytes == null)
                {
                    throw new SteamInvalidBranchPasswordException(manifest.AppId, manifest.DepotId, manifest.BranchName,
                        branchPassword);
                }

                return new DepotManifest(manifest.BranchName, BitConverter.ToUInt64(manifestIdBytes));
            }
            else
            {
                var result = await SteamApps.CheckAppBetaPassword(manifest.AppId, branchPassword);

                if (!result.BetaPasswords.Any(x => x.Key == manifest.BranchName))
                {
                    throw new SteamInvalidBranchPasswordException(manifest.AppId, manifest.DepotId, manifest.BranchName,
                        branchPassword);
                }

                var manifestCryptoInput = DecodeHexString(manifest.EncryptedManifestId);

                try
                {
                    var manifestIdBytes = SteamKit.CryptoHelper.SymmetricDecryptECB(manifestCryptoInput,
                        result.BetaPasswords[manifest.BranchName]);

                    return new DepotManifest(manifest.BranchName, BitConverter.ToUInt64(manifestIdBytes));
                }
                catch (Exception ex)
                {
                    throw new SteamInvalidBranchPasswordException(manifest.AppId, manifest.DepotId, manifest.BranchName,
                        branchPassword, ex);
                }
            }
        }

#nullable enable
        public async Task<ManifestId> GetDepotDefaultManifestIdAsync(AppId appId, DepotId depotId,
            string branch = "public", string? branchPassword = null)
#nullable disable
        {
            var appInfo = await GetAppInfoAsync(appId);
            var depots = appInfo.KeyValues.Children.First(x => x.Name == "depots");
            var depot = depots[depotId.ToString()];
            var manifest = depot["manifests"][branch];
            var privateManifest = depot["encryptedmanifests"][branch];

            if (depot["manifests"] == SteamKit.KeyValue.Invalid && depot["depotfromapp"] != SteamKit.KeyValue.Invalid)
            {
                return await GetDepotDefaultManifestIdAsync(depot["depotfromapp"].AsUnsignedInteger(), depotId, branch);
            }

            if (depot == SteamKit.KeyValue.Invalid)
            {
                throw new SteamDepotNotFoundException(depotId);
            }

            if (manifest == SteamKit.KeyValue.Invalid && privateManifest == SteamKit.KeyValue.Invalid)
            {
                throw new SteamBranchNotFoundException(appId, depotId, branch);
            }

            if (manifest != SteamKit.KeyValue.Invalid)
            {
                return ulong.Parse(manifest.Value);
            }

            if (privateManifest != SteamKit.KeyValue.Invalid)
            {
                var encryptedVersion1 = privateManifest["encrypted_gid"];
                var encryptedVersion2 = privateManifest["encrypted_gid_2"];

                if (encryptedVersion1 != SteamKit.KeyValue.Invalid)
                {
                    var manifestCryptoInput = DecodeHexString(encryptedVersion1.Value);
                    var manifestIdBytes =
                        SteamKit.CryptoHelper.VerifyAndDecryptPassword(manifestCryptoInput, branchPassword);

                    if (manifestIdBytes == null)
                    {
                        throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword);
                    }

                    return BitConverter.ToUInt64(manifestIdBytes);
                }

                if (encryptedVersion2 != SteamKit.KeyValue.Invalid)
                {
                    var result = await SteamApps.CheckAppBetaPassword(appId, branchPassword);

                    if (!result.BetaPasswords.Any(x => x.Key == branch))
                    {
                        throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword);
                    }

                    var manifestCryptoInput = DecodeHexString(encryptedVersion2.Value);

                    try
                    {
                        var manifestIdBytes =
                            SteamKit.CryptoHelper.SymmetricDecryptECB(manifestCryptoInput,
                                result.BetaPasswords[branch]);

                        return BitConverter.ToUInt64(manifestIdBytes);
                    }
                    catch (Exception ex)
                    {
                        throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword, ex);
                    }
                }
            }

            throw new ArgumentException(
                $"Combination of app id = {appId}, depot id = {depotId} and branch = {branch} could not be found.");
        }

        internal async Task<IDownloadHandler> GetAppDataInternalAsync(AppId appId, DepotId? depotId,
            ManifestId? manifestId, string branch = "public", string branchPassword = null, SteamOs? os = null,
            bool isUserGeneratedContent = false)
        {
            if (!await GetHasAccessAsync(appId, depotId))
            {
                var gotFreeLicense = await GetFreeLicenseAsync(appId);

                if (!gotFreeLicense)
                {
                    throw new SteamRequiredLicenseNotFoundException(appId);
                }
            }

            var appInfo = await GetAppInfoAsync(appId);
            var depots = appInfo.KeyValues.Children.First(x => x.Name == "depots");

            if (isUserGeneratedContent)
            {
                depotId = depots["workshopdepot"].AsUnsignedInteger();
            }

            // Check if given depot id exists
            if (!depots.Children.ContainsKeyOrValue(depotId.ToString(), out var ignore))
            {
                throw new SteamDepotNotFoundException(depotId.Value);
            }

            depots.Children.ContainsKeyOrValue(depotId.ToString(), out var depotKeyValues);

            // Branch and OS check
            if (!isUserGeneratedContent)
            {
                var depotConfig = depotKeyValues["config"];
                var depotOsList = depotConfig["oslist"];

                if (depotOsList != SteamKit.KeyValue.Invalid)
                {
                    var supportedOs = depotOsList.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);

                    if (!supportedOs.Contains(os.Identifier))
                    {
                        throw new SteamOsNotSupportedByAppException(appId, depotId.Value, manifestId.Value, os,
                            supportedOs);
                    }
                }
            }


            return new MultipleFilesHandler(this,
                await GetManifestAsync(appId, depotId.Value, manifestId.Value, branch, branchPassword,
                    CancellationToken), appId, depotId.Value, manifestId.Value);
        }

        internal async Task<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(AppId appId)
        {
            await RequestAccessTokenIfNecessaryAsync(appId);

            var result = await GetProductInfoAsync(new List<AppId> {appId}, new List<uint>());

            return result.Apps[appId];
        }

        internal async Task RequestAccessTokenIfNecessaryAsync(AppId appId)
        {
            if (_productAccessKeys.ContainsKey(appId))
            {
                return;
            }

            var accessTokenRequestResult =
                await SteamApps.PICSGetAccessTokens(new List<uint> {appId}, new List<uint>());

            if (accessTokenRequestResult.AppTokensDenied.Contains(appId))
            {
                throw new SteamAppAccessTokenDeniedException(appId);
            }

            _productAccessKeys.AddOrUpdate(appId, accessTokenRequestResult.AppTokens[appId],
                (appId, val) => accessTokenRequestResult.AppTokens[appId]);
        }

        internal async Task<SteamKit.SteamApps.PICSProductInfoCallback> GetProductInfoAsync(List<AppId> appIds,
            List<uint> packageIds)
        {
            var appRequests = new List<SteamKit.SteamApps.PICSRequest>();
            var packageRequests = new List<SteamKit.SteamApps.PICSRequest>();

            foreach (var appId in appIds)
            {
                await RequestAccessTokenIfNecessaryAsync(appId);

                appRequests.Add(new SteamKit.SteamApps.PICSRequest
                {
                    ID = appId,
                    AccessToken = _productAccessKeys[appId]
                });
            }

            foreach (var packageId in packageIds)
            {
                packageRequests.Add(new SteamKit.SteamApps.PICSRequest
                {
                    ID = packageId
                });
            }

            var result = await SteamApps.PICSGetProductInfo(appRequests, packageRequests);

            return result.Results.First();
        }

        internal async Task<bool> GetHasAccessAsync(AppId appId, DepotId? depotId)
        {
            List<uint> packageIds = null;

            if (SteamUser.SteamID.AccountType == SteamKit.EAccountType.AnonUser)
            {
                packageIds = new List<uint> {17906};
            }
            else
            {
                packageIds = SteamClient.Licenses.Select(x => x.PackageID).ToList();
            }

            var packageInfos = await GetProductInfoAsync(new List<AppId> {appId}, packageIds);

            return packageInfos.Apps.ContainsKey(appId);

            //foreach (var packageInfo in packageInfos)
            //{
            //    if (packageInfo.KeyValues["appids"].Children.Any(x => x.AsUnsignedInteger() == appId))
            //    {
            //        return true;
            //    }

            //    if (depotId.HasValue)
            //    {
            //        if (packageInfo.KeyValues["depotids"].Children.Any(x => x.AsUnsignedInteger() == depotId.Value))
            //        {
            //            return true;
            //        }
            //    }
            //}

            //return false;
        }

        internal async Task<bool> GetFreeLicenseAsync(AppId appId)
        {
            var result = await SteamApps.RequestFreeLicense(appId);

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