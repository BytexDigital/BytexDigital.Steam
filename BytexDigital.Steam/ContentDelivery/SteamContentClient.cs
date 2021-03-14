using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;
using BytexDigital.Steam.Extensions;

using SteamKit2;
using SteamKit2.Internal;
using SteamKit2.Unified.Internal;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using SteamKit = SteamKit2;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamContentClient
    {
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;
        public Core.SteamClient SteamClient { get; }

        internal SteamUnifiedMessages.UnifiedService<IPublishedFile> PublishedFileService { get; }
        internal SteamUser SteamUser { get; }
        internal SteamApps SteamApps { get; }
        internal SteamUnifiedMessages SteamUnifiedMessagesService { get; }
        internal SteamCdnServerPool SteamCdnServerPool { get; }
        internal int MaxConcurrentDownloadsPerTask { get; }
        internal ulong ChunkBufferSize { get; }
        internal double BufferUsageThreshold { get; }

        private readonly ConcurrentDictionary<AppId, ulong> _productAccessKeys = new ConcurrentDictionary<AppId, ulong>();
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly SteamContentServerQualityProvider _steamContentServerQualityProvider;

        public SteamContentClient(Core.SteamClient steamClient,
                                  SteamContentServerQualityProvider steamContentServerQualityProvider = null,
                                  int maxConcurrentDownloadsPerTask = 10,
                                  ulong chunkBufferSize = 3221225472,
                                  double bufferUsageThreshold = 1)
        {
            SteamClient = steamClient;
            _steamContentServerQualityProvider = steamContentServerQualityProvider ?? new SteamContentServerQualityNoMemoryProvider();
            SteamUnifiedMessagesService = SteamClient.InternalClient.GetHandler<SteamKit.SteamUnifiedMessages>();
            PublishedFileService = SteamUnifiedMessagesService.CreateService<SteamKit.Unified.Internal.IPublishedFile>();
            SteamCdnServerPool = new SteamCdnServerPool(this, _steamContentServerQualityProvider);
            MaxConcurrentDownloadsPerTask = maxConcurrentDownloadsPerTask;
            ChunkBufferSize = chunkBufferSize;
            BufferUsageThreshold = bufferUsageThreshold;
            SteamApps = SteamClient.InternalClient.GetHandler<SteamKit.SteamApps>();
            SteamUser = SteamClient.InternalClient.GetHandler<SteamKit.SteamUser>();

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(SteamClient.CancellationToken);
        }

        public async Task<Manifest> GetManifestAsync(AppId appId, DepotId depotId, ManifestId manifestId, CancellationToken cancellationToken = default)
        {
            await SteamClient.AwaitReadyAsync(cancellationToken);

            Exception lastEx = null;

            for (int i = 0; i < 30; i++)
            {
                SteamCdnClient steamCdnClient;

                try
                {
                    steamCdnClient = await SteamCdnServerPool.GetClientAsync();

                    //await cdnClientWrapper.CdnClient.AuthenticateDepotAsync(depotId);

                    var manifest = await steamCdnClient.DownloadManifestAsync(appId, depotId, manifestId);

                    if (manifest.FilenamesEncrypted)
                    {
                        var depotKey = await steamCdnClient.GetDepotKeyAsync(depotId, appId);
                        manifest.DecryptFilenames(depotKey);
                    }

                    return manifest;
                }
                catch (SteamDepotAccessDeniedException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    lastEx = ex;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            throw new SteamManifestDownloadException(lastEx);
        }

        public async Task<PublishedFileDetails> GetPublishedFileDetailsAsync(PublishedFileId publishedFileId)
        {
            var request = new CPublishedFile_GetDetails_Request();
            request.publishedfileids.Add(publishedFileId);

            var result = await PublishedFileService.SendMessage(api => api.GetDetails(request));

            if (result.Result == EResult.OK)
            {
                var details = result.GetDeserializedResponse<CPublishedFile_GetDetails_Response>().publishedfiledetails.First();

                return details;
            }

            throw new SteamPublishedFileDetailsFetchException(result.Result);
        }

        public async Task<IList<PublishedFileDetails>> GetPublishedFilesForAppIdRawAsync(AppId appId, CancellationToken? cancellationToken = null)
        {
            cancellationToken = cancellationToken ?? CancellationToken.None;

            string paginationCursor = "*";
            List<PublishedFileDetails> items = new List<PublishedFileDetails>();

            while (!cancellationToken.Value.IsCancellationRequested)
            {
                var requestDetails = new CPublishedFile_QueryFiles_Request
                {
                    query_type = (uint)EPublishedFileQueryType.RankedByPublicationDate,
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
                    return_short_description = true,
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

#nullable enable
        public async Task<IDownloadHandler> GetPublishedFileDataAsync(PublishedFileId publishedFileId, ManifestId? manifestId = null, string? branch = null, string? branchPassword = null, SteamOs? os = null)
#nullable disable
        {
            var publishedFileDetails = await GetPublishedFileDetailsAsync(publishedFileId);

            if (!string.IsNullOrEmpty(publishedFileDetails.file_url))
            {
                return new DirectFileHandler(publishedFileDetails.file_url, publishedFileDetails.filename);
            }
            else
            {
                return await GetAppDataInternalAsync(
                    publishedFileDetails.consumer_appid,
                    publishedFileDetails.consumer_appid,
                    manifestId ?? publishedFileDetails.hcontent_file,
                    branch,
                    os,
                    true);
            }
        }

#nullable enable
        public async Task<IDownloadHandler> GetAppDataAsync(AppId appId, DepotId depotId, ManifestId? manifestId = null, string branch = "public", string? branchPassword = null, SteamOs? os = null)
#nullable disable
        {
            if (!manifestId.HasValue)
            {
                manifestId = await GetDepotDefaultManifestIdAsync(appId, depotId, branch, branchPassword);
            }

            return await GetAppDataInternalAsync(appId, depotId, manifestId.Value, branch, os ?? SteamClient.GetSteamOs(), false);
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
                bool isDepot = uint.TryParse(depot.Name, out uint depotId);

                if (!isDepot) continue;

                string name = depot["name"].Value;
                ulong maxSize = depot["maxsize"].AsUnsignedLong(0);
                bool isSharedInstall = depot["sharedinstall"].AsBoolean(false);
                AppId? depotFromApp = depot["depotfromapp"].AsUnsignedInteger(default);

                if (depotFromApp.Value.Id == default)
                {
                    depotFromApp = null;
                }

                List<SteamOs> operatingSystems = new List<SteamOs>();
                Dictionary<string, string> configEntries = new Dictionary<string, string>();

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

                List<Models.DepotManifest> manifests = depot["manifests"]
                    .Children
                    .Select(x => new Models.DepotManifest(x.Name, x.AsUnsignedLong()))
                    .ToList();

                List<Models.DepotEncryptedManifest> encryptedManifests = depot["encryptedmanifests"]
                    .Children
                    .Select(manifest =>
                    {
                        if (manifest["encrypted_gid_2"] != KeyValue.Invalid)
                        {
                            return new DepotEncryptedManifest(appId, depotId, manifest.Name, manifest["encrypted_gid_2"].Value, DepotEncryptedManifest.EncryptionVersion.V2);
                        }
                        else
                        {
                            return new DepotEncryptedManifest(appId, depotId, manifest.Name, manifest["encrypted_gid"].Value, DepotEncryptedManifest.EncryptionVersion.V1);
                        }
                    })
                    .ToList();

                parsedDepots.Add(new Depot(depotId, name, maxSize, operatingSystems, manifests, encryptedManifests, configEntries, isSharedInstall, depotFromApp));
            }

            return parsedDepots;
        }

        public async Task<IReadOnlyList<Branch>> GetBranchesAsync(AppId appId)
        {
            var appInfo = await GetAppInfoAsync(appId);
            var depots = appInfo.KeyValues.Children.First(x => x.Name == "depots");
            var branches = depots["branches"];

            List<Branch> parsedBranches = new List<Branch>();

            foreach (var branch in branches.Children)
            {
                var name = branch.Name;
                ulong buildid = branch["buildid"].AsUnsignedLong(0);
                string description = branch["description"].AsString();
                bool requiresPassword = branch["pwdrequired"].AsBoolean(false);
                long timeUpdated = branch["timeupdated"].AsLong();

                parsedBranches.Add(new Branch(name, buildid, description, requiresPassword, DateTimeOffset.FromUnixTimeSeconds(timeUpdated)));
            }

            return parsedBranches;
        }

        public async Task<Models.DepotManifest> DecryptDepotManifestAsync(DepotEncryptedManifest manifest, string branchPassword)
        {
            if (manifest.Version == DepotEncryptedManifest.EncryptionVersion.V1)
            {
                byte[] manifestCryptoInput = DecodeHexString(manifest.EncryptedManifestId);
                byte[] manifestIdBytes = SteamKit.CryptoHelper.VerifyAndDecryptPassword(manifestCryptoInput, branchPassword);

                if (manifestIdBytes == null)
                {
                    throw new SteamInvalidBranchPasswordException(manifest.AppId, manifest.DepotId, manifest.BranchName, branchPassword);
                }

                return new Models.DepotManifest(manifest.BranchName, BitConverter.ToUInt64(manifestIdBytes));
            }
            else
            {
                var result = await SteamApps.CheckAppBetaPassword(manifest.AppId, branchPassword);

                if (!result.BetaPasswords.Any(x => x.Key == manifest.BranchName)) throw new SteamInvalidBranchPasswordException(manifest.AppId, manifest.DepotId, manifest.BranchName, branchPassword);

                byte[] manifestCryptoInput = DecodeHexString(manifest.EncryptedManifestId);

                try
                {
                    byte[] manifestIdBytes = SteamKit.CryptoHelper.SymmetricDecryptECB(manifestCryptoInput, result.BetaPasswords[manifest.BranchName]);

                    return new Models.DepotManifest(manifest.BranchName, BitConverter.ToUInt64(manifestIdBytes));
                }
                catch (Exception ex)
                {
                    throw new SteamInvalidBranchPasswordException(manifest.AppId, manifest.DepotId, manifest.BranchName, branchPassword, ex);
                }
            }
        }

#nullable enable
        public async Task<ManifestId> GetDepotDefaultManifestIdAsync(AppId appId, DepotId depotId, string branch = "public", string? branchPassword = null)
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

            if (depot == KeyValue.Invalid) throw new SteamDepotNotFoundException(depotId);
            if (manifest == KeyValue.Invalid && privateManifest == KeyValue.Invalid) throw new SteamBranchNotFoundException(appId, depotId, branch);

            if (manifest != SteamKit.KeyValue.Invalid)
            {
                return ulong.Parse(manifest.Value);
            }
            else if (privateManifest != SteamKit.KeyValue.Invalid)
            {
                var encryptedVersion1 = privateManifest["encrypted_gid"];
                var encryptedVersion2 = privateManifest["encrypted_gid_2"];

                if (encryptedVersion1 != SteamKit.KeyValue.Invalid)
                {
                    byte[] manifestCryptoInput = DecodeHexString(encryptedVersion1.Value);
                    byte[] manifestIdBytes = SteamKit.CryptoHelper.VerifyAndDecryptPassword(manifestCryptoInput, branchPassword);

                    if (manifestIdBytes == null)
                    {
                        throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword);
                    }

                    return BitConverter.ToUInt64(manifestIdBytes);
                }
                else if (encryptedVersion2 != SteamKit.KeyValue.Invalid)
                {
                    var result = await SteamApps.CheckAppBetaPassword(appId, branchPassword);

                    if (!result.BetaPasswords.Any(x => x.Key == branch)) throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword);

                    byte[] manifestCryptoInput = DecodeHexString(encryptedVersion2.Value);

                    try
                    {
                        byte[] manifestIdBytes = SteamKit.CryptoHelper.SymmetricDecryptECB(manifestCryptoInput, result.BetaPasswords[branch]);

                        return BitConverter.ToUInt64(manifestIdBytes);
                    }
                    catch (Exception ex)
                    {
                        throw new SteamInvalidBranchPasswordException(appId, depotId, branch, branchPassword, ex);
                    }
                }
            }

            throw new ArgumentException($"Combination of app id = {appId}, depot id = {depotId} and branch = {branch} could not be found.");
        }

#nullable enable
        internal async Task<IDownloadHandler> GetAppDataInternalAsync(AppId appId, DepotId? depotId, ManifestId? manifestId, string branch = "public", SteamOs? os = null, bool isUserGeneratedContent = false)
#nullable disable
        {
            if (!await GetHasAccessAsync(appId, depotId))
            {
                bool gotFreeLicense = await GetFreeLicenseAsync(appId);

                if (!gotFreeLicense) throw new SteamRequiredLicenseNotFoundException(appId);
            }

            var appInfo = await GetAppInfoAsync(appId);
            var depots = appInfo.KeyValues.Children.First(x => x.Name == "depots");

            if (isUserGeneratedContent)
            {
                depotId = depots["workshopdepot"].AsUnsignedInteger();
            }

            // Check if given depot id exists
            if (!depots.Children.ContainsKeyOrValue(depotId.ToString(), out var ignore)) throw new SteamDepotNotFoundException(depotId.Value);

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
                        throw new SteamOsNotSupportedByAppException(appId, depotId.Value, manifestId.Value, os, supportedOs);
                    }
                }
            }


            return new MultipleFilesHandler(this, await GetManifestAsync(appId, depotId.Value, manifestId.Value, CancellationToken), appId, depotId.Value, manifestId.Value);
        }

        internal async Task<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(AppId appId)
        {
            if (!_productAccessKeys.ContainsKey(appId))
            {
                var accessTokenRequestResult = await SteamApps.PICSGetAccessTokens(new List<uint> { appId }, new List<uint>());

                if (accessTokenRequestResult.AppTokensDenied.Contains(appId)) throw new SteamAppAccessTokenDeniedException(appId);

                _productAccessKeys.AddOrUpdate(appId, accessTokenRequestResult.AppTokens[appId], (appId, val) => accessTokenRequestResult.AppTokens[appId]);
            }

            var result = await SteamApps.PICSGetProductInfo(appId, null, onlyPublic: false);

            return result.Results.First().Apps[appId];
        }

        internal async Task<IList<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>> GetPackageInfosAsync(IEnumerable<uint> packageIds)
        {
            var result = await SteamApps.PICSGetProductInfo(new List<uint>(), packageIds);

            return result.Results.First().Packages.Select(x => x.Value).ToList();
        }

        internal async Task<bool> GetHasAccessAsync(AppId? appId, DepotId? depotId)
        {
            IEnumerable<uint> query = null;

            if (SteamUser.SteamID.AccountType == SteamKit.EAccountType.AnonUser)
            {
                query = new List<uint> { 17906 };
            }
            else
            {
                query = SteamClient.Licenses.Select(x => x.PackageID);
            }

            var packageInfos = await GetPackageInfosAsync(query);

            foreach (var packageInfo in packageInfos)
            {
                if (appId.HasValue)
                {
                    if (packageInfo.KeyValues["appids"].Children.Any(x => x.AsUnsignedInteger() == appId.Value))
                    {
                        return true;
                    }
                }

                if (depotId.HasValue)
                {
                    if (packageInfo.KeyValues["depotids"].Children.Any(x => x.AsUnsignedInteger() == depotId.Value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal async Task<bool> GetFreeLicenseAsync(AppId appId)
        {
            var result = await SteamApps.RequestFreeLicense(appId);

            return result.GrantedApps.Contains(appId);
        }

        private static byte[] DecodeHexString(string hex)
        {
            if (hex == null) return null;

            int chars = hex.Length;
            byte[] bytes = new byte[chars / 2];

            for (int i = 0; i < chars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);

            return bytes;
        }

    }
}
