using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;
using BytexDigital.Steam.Extensions;

using SteamKit2;
using SteamKit2.Internal;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        internal ConcurrentDictionary<string, SteamApps.CDNAuthTokenCallback> CdnAuthenticationTokens { get; }
        internal int MaxConcurrentDownloadsPerTask { get; }

        private readonly ConcurrentDictionary<AppId, ulong> _productAccessKeys = new ConcurrentDictionary<AppId, ulong>();
        private readonly ConcurrentDictionary<DepotId, byte[]> _depotKeys = new ConcurrentDictionary<DepotId, byte[]>();
        private readonly CancellationTokenSource _cancellationTokenSource;

        public SteamContentClient(Core.SteamClient steamClient, int maxConcurrentDownloadsPerTask = 10)
        {
            SteamClient = steamClient;
            SteamUnifiedMessagesService = SteamClient.InternalClient.GetHandler<SteamKit.SteamUnifiedMessages>();
            PublishedFileService = SteamUnifiedMessagesService.CreateService<IPublishedFile>();
            CdnAuthenticationTokens = new ConcurrentDictionary<string, SteamApps.CDNAuthTokenCallback>();
            MaxConcurrentDownloadsPerTask = maxConcurrentDownloadsPerTask;
            SteamApps = SteamClient.InternalClient.GetHandler<SteamKit.SteamApps>();
            SteamUser = SteamClient.InternalClient.GetHandler<SteamKit.SteamUser>();

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(SteamClient.CancellationToken);
        }

        public async Task<Manifest> GetManifestAsync(AppId appId, DepotId depotId, ManifestId manifestId, string branch = null, string branchPassword = null, CancellationToken cancellationToken = default)
        {
            await SteamClient.AwaitReadyAsync(cancellationToken);

            var pool = new SteamCdnServerPool(this, appId);

            int attempts = 0;
            const int maxAttempts = 10;

            while (attempts < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var server = await pool.GetServerAsync(cancellationToken);
                    var depotKey = await GetDepotKeyAsync(depotId, appId);
                    var cdnKey = await pool.AuthenticateWithServerAsync(depotId, server);
                    var manifestCode = await SteamClient._steamContentHandler.GetManifestRequestCode(depotId, appId, manifestId);
                    var manifest = await pool.CdnClient.DownloadManifestAsync(depotId, manifestId, manifestCode, server, depotKey);


                    if (manifest.FilenamesEncrypted)
                    {
                        manifest.DecryptFilenames(depotKey);
                    }

                    pool.ReturnServer(server, isFaulty: false);

                    return manifest;
                }
                catch (TaskCanceledException)
                {
                    throw;
                }
                catch (SteamDepotAccessDeniedException)
                {
                    throw;
                }
                catch (SteamKitWebRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                                                             ex.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                                             ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    throw new SteamManifestDownloadException(ex);
                }
                catch (Exception)
                {
                    // Retry..
                }
                finally
                {
                    attempts += 1;
                }
            }

            try
            {
                pool.Close();
            } catch { }

            cancellationToken.ThrowIfCancellationRequested();

            throw new SteamManifestDownloadException($"Could not download the manifest = {manifestId} after {maxAttempts} attempts.");
        }

        public async Task<byte[]> GetDepotKeyAsync(DepotId depotId, AppId appId)
        {
            if (_depotKeys.ContainsKey(depotId))
            {
                return _depotKeys[depotId];
            }

            var result = await SteamApps.GetDepotDecryptionKey(depotId, appId);

            if (result.Result != EResult.OK)
            {
                if (result.Result == EResult.AccessDenied)
                {
                    throw new SteamDepotAccessDeniedException(depotId);
                }

                throw new SteamDepotKeyNotRetrievableException(depotId, result.Result);
            }

            while (!_depotKeys.TryAdd(depotId, result.DepotKey)) { }

            return result.DepotKey;
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
                    query_type = (uint)SteamKit.EPublishedFileQueryType.RankedByPublicationDate,
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

        public async Task<IDownloadHandler> GetPublishedFileDataAsync(PublishedFileId publishedFileId, ManifestId? manifestId = null, string? branch = null, string? branchPassword = null, SteamOs? os = null)
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
            else
            {
                return await GetAppDataInternalAsync(
                    publishedFileDetails.consumer_appid,
                    publishedFileDetails.consumer_appid,
                    manifestId ?? publishedFileDetails.hcontent_file,
                    branch,
                    branchPassword,
                    os,
                    true);
            }
        }

        public async Task<IDownloadHandler> GetAppDataAsync(AppId appId, DepotId depotId, ManifestId? manifestId = null, string branch = "public", string? branchPassword = null, SteamOs? os = null)
        {
            if (!manifestId.HasValue)
            {
                manifestId = await GetDepotDefaultManifestIdAsync(appId, depotId, branch, branchPassword);
            }

            return await GetAppDataInternalAsync(appId, depotId, manifestId.Value, branch, branchPassword, os ?? SteamClient.GetSteamOs(), false);
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

        internal async Task<IDownloadHandler> GetAppDataInternalAsync(AppId appId, DepotId? depotId, ManifestId? manifestId, string branch = "public", string branchPassword = null, SteamOs? os = null, bool isUserGeneratedContent = false)
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


            return new MultipleFilesHandler(this, await GetManifestAsync(appId, depotId.Value, manifestId.Value, branch, branchPassword, CancellationToken), appId, depotId.Value, manifestId.Value);
        }

        internal async Task<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(AppId appId)
        {
            await RequestAccessTokenIfNecessaryAsync(appId);

            var result = await GetProductInfoAsync(new List<AppId> { appId }, new List<uint>());

            return result.Apps[appId];
        }

        internal async Task RequestAccessTokenIfNecessaryAsync(AppId appId)
        {
            if (_productAccessKeys.ContainsKey(appId)) return;

            var accessTokenRequestResult = await SteamApps.PICSGetAccessTokens(new List<uint> { appId }, new List<uint>());

            if (accessTokenRequestResult.AppTokensDenied.Contains(appId)) throw new SteamAppAccessTokenDeniedException(appId);

            _productAccessKeys.AddOrUpdate(appId, accessTokenRequestResult.AppTokens[appId], (appId, val) => accessTokenRequestResult.AppTokens[appId]);
        }

        internal async Task<SteamApps.PICSProductInfoCallback> GetProductInfoAsync(List<AppId> appIds, List<uint> packageIds)
        {
            List<SteamApps.PICSRequest> appRequests = new List<SteamApps.PICSRequest>();
            List<SteamApps.PICSRequest> packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var appId in appIds)
            {
                await RequestAccessTokenIfNecessaryAsync(appId);

                appRequests.Add(new SteamApps.PICSRequest
                {
                    ID = appId,
                    AccessToken = _productAccessKeys[appId]
                });
            }

            foreach (var packageId in packageIds)
            {
                packageRequests.Add(new SteamApps.PICSRequest
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
                packageIds = new List<uint> { 17906 };
            }
            else
            {
                packageIds = SteamClient.Licenses.Select(x => x.PackageID).ToList();
            }

            var packageInfos = (await GetProductInfoAsync(new List<AppId> { appId }, packageIds));

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
            if (hex == null) return null;

            int chars = hex.Length;
            byte[] bytes = new byte[chars / 2];

            for (int i = 0; i < chars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);

            return bytes;
        }

    }
}
