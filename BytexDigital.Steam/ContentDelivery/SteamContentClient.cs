using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;
using BytexDigital.Steam.Extensions;

using SteamKit2;
using SteamKit2.Unified.Internal;

using System;
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
        internal SteamCdnClientPool SteamCdnClientPool { get; }
        internal int MaxConcurrentDownloadsPerTask { get; }

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly SteamContentServerQualityProvider _steamContentServerQualityProvider;

        public SteamContentClient(Core.SteamClient steamClient, SteamContentServerQualityProvider steamContentServerQualityProvider = null, int maxConcurrentDownloadsPerTask = 10)
        {
            SteamClient = steamClient;
            _steamContentServerQualityProvider = steamContentServerQualityProvider ?? new SteamContentServerQualityNoMemoryProvider();
            SteamUnifiedMessagesService = SteamClient.InternalClient.GetHandler<SteamKit.SteamUnifiedMessages>();
            PublishedFileService = SteamUnifiedMessagesService.CreateService<SteamKit.Unified.Internal.IPublishedFile>();
            SteamCdnClientPool = new SteamCdnClientPool(this, _steamContentServerQualityProvider);
            MaxConcurrentDownloadsPerTask = maxConcurrentDownloadsPerTask;

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
                SteamCdnClientPool.CdnClient cdnClientWrapper = null;

                try
                {
                    cdnClientWrapper = await SteamCdnClientPool.GetClient(appId, depotId);

                    //await cdnClientWrapper.CdnClient.AuthenticateDepotAsync(depotId);

                    var manifest = await cdnClientWrapper.InternalCdnClient.DownloadManifestAsync(depotId, manifestId, cdnClientWrapper.ServerWrapper.Server);

                    if (manifest.FilenamesEncrypted)
                    {
                        var depotKey = await SteamCdnClientPool.GetDepotKeyAsync(depotId, appId);
                        manifest.DecryptFilenames(depotKey);
                    }

                    SteamCdnClientPool.ReturnClient(cdnClientWrapper);

                    return manifest;
                }
                catch (SteamDepotAccessDeniedException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    SteamCdnClientPool.ReturnClient(cdnClientWrapper);
                    lastEx = ex;
                }
                catch (Exception ex)
                {
                    SteamCdnClientPool.ReturnClient(cdnClientWrapper);
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

        public async Task<IList<PublishedFileDetails>> GetPublishedFilesForAppIdAsync(AppId appId, CancellationToken? cancellationToken = null)
        {
            cancellationToken = cancellationToken ?? CancellationToken.None;

            string paginationCursor = null;
            List<PublishedFileDetails> items = new List<PublishedFileDetails>();

            while (!cancellationToken.Value.IsCancellationRequested)
            {
                var methodResponse = await PublishedFileService.SendMessage(api => api.QueryFiles(new CPublishedFile_QueryFiles_Request
                {
                    appid = appId,
                    return_vote_data = true,
                    return_children = true,
                    return_for_sale_data = true,
                    return_kv_tags = true,
                    return_metadata = true,
                    return_tags = true,
                    return_previews = true,
                    return_details = true,
                    return_short_description = true,
                    page = 1,
                    numperpage = 100,
                    query_type = 11,
                    filetype = (uint)EWorkshopFileType.GameManagedItem,
                    cursor = paginationCursor
                }));

                var returnedItems = methodResponse.GetDeserializedResponse<CPublishedFile_QueryFiles_Response>();

                items.AddRange(returnedItems.publishedfiledetails);
                paginationCursor = returnedItems.next_cursor;

                if (paginationCursor == null)
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
            if (!await GetHasAccessAsync(appId))
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


            return new FilesHandler(this, await GetManifestAsync(appId, depotId.Value, manifestId.Value, CancellationToken), appId, depotId.Value, manifestId.Value);
        }

        internal async Task<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppInfoAsync(AppId appId)
        {
            var accessTokenRequestResult = await SteamApps.PICSGetAccessTokens(new List<uint> { appId }, new List<uint>());

            if (accessTokenRequestResult.AppTokensDenied.Contains(appId)) throw new SteamAppAccessTokenDeniedException(appId);

            //var accessToken = accessTokenRequestResult.AppTokens[appId];

            var result = await SteamApps.PICSGetProductInfo(appId, null, onlyPublic: false);

            return result.Results.First().Apps[appId];
        }

        internal async Task<IList<SteamKit.SteamApps.PICSProductInfoCallback.PICSProductInfo>> GetPackageInfosAsync(IEnumerable<uint> packageIds)
        {
            var result = await SteamApps.PICSGetProductInfo(new List<uint>(), packageIds);

            return result.Results.First().Packages.Select(x => x.Value).ToList();
        }

        internal async Task<bool> GetHasAccessAsync(AppId appId)
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
                if (packageInfo.KeyValues["appids"].Children.Any(x => x.AsUnsignedInteger() == appId))
                {
                    return true;
                }

                if (packageInfo.KeyValues["depotids"].Children.Any(x => x.AsUnsignedInteger() == appId))
                {
                    return true;
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
