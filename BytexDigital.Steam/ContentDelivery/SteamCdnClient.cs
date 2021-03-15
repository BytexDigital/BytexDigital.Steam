//using BytexDigital.Steam.ContentDelivery.Exceptions;
//using BytexDigital.Steam.ContentDelivery.Models;
//using BytexDigital.Steam.Core.Structs;

//using SteamKit2;

//using System;
//using System.Collections.Concurrent;
//using System.Linq;
//using System.Net.Http;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//using static SteamKit2.CDNClient;
//using static SteamKit2.DepotManifest;

//namespace BytexDigital.Steam.ContentDelivery
//{
//    public class SteamCdnClient
//    {
//        private readonly SteamContentClient _steamContentClient;
//        private readonly SteamCdnServer _server;
//        private static readonly ConcurrentDictionary<DepotId, byte[]> _depotKeys = new ConcurrentDictionary<DepotId, byte[]>();
//        private readonly ConcurrentDictionary<(AppId, DepotId), CdnClientAuthToken> _authTokens = new ConcurrentDictionary<(AppId, DepotId), CdnClientAuthToken>();
//        private static readonly HttpClient _httpClient = new HttpClient();

//        public int Errors { get; set; }

//        public CDNClient InternalCdnClient { get; private set; }

//        public SteamCdnClient(SteamContentClient steamContentClient, SteamCdnServer server)
//        {
//            _steamContentClient = steamContentClient;
//            _server = server;

//            InternalCdnClient = new CDNClient(steamContentClient.SteamClient.InternalClient);
//        }

//        public async Task<SteamKit2.DepotManifest> DownloadManifestAsync(AppId appId, DepotId depotId, ManifestId manifestId)
//        {
//            await AuthenticateCdnClientAsync(appId, depotId);

//            return await InternalCdnClient.DownloadManifestAsync(depotId, manifestId, _server.InternalServer);
//        }

//        public async Task<byte[]> DownloadChunkAsync(AppId appId, DepotId depotId, ChunkData chunk, CancellationToken cancellationToken)
//        {
//            //await AuthenticateCdnClientAsync(appId, depotId);

//            _authTokens.TryGetValue((appId, depotId), out var authKey);

//            var uriBuilder = new UriBuilder
//            {
//                Scheme = _server.InternalServer.Protocol == Server.ConnectionProtocol.HTTP ? "http" : "https",
//                Host = _server.InternalServer.VHost,
//                Port = _server.InternalServer.Port,
//                Path = $"depot/{depotId}/chunk/{chunk.ChunkID.Aggregate(new StringBuilder(), (sb, v) => sb.Append(v.ToString("x2")))}",
//                Query = authKey.Token
//            };

//            var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);

//            using (var cts = new CancellationTokenSource())
//            {
//                cts.CancelAfter(RequestTimeout);

//                try
//                {
//                    var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

//                    if (!response.IsSuccessStatusCode)
//                    {
//                        throw new SteamKitWebRequestException($"Response status code does not indicate success: {response.StatusCode:D} ({response.ReasonPhrase}).", response);
//                    }

//                    var responseData = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
//                    return responseData;
//                }
//                catch (Exception ex)
//                {
//                    throw;
//                }
//            }
//        }

//        public async Task<byte[]> GetDepotKeyAsync(DepotId depotId, AppId appId)
//        {
//            if (_depotKeys.ContainsKey(depotId))
//            {
//                return _depotKeys[depotId];
//            }

//            var result = await _steamContentClient.SteamApps.GetDepotDecryptionKey(depotId, appId);

//            if (result.Result != EResult.OK)
//            {
//                if (result.Result == EResult.AccessDenied)
//                {
//                    throw new SteamDepotAccessDeniedException(depotId);
//                }

//                throw new SteamDepotKeyNotRetrievableException(depotId, result.Result);
//            }

//            while (!_depotKeys.TryAdd(depotId, result.DepotKey)) { }

//            return result.DepotKey;
//        }

//        public async Task AuthenticateCdnClientAsync(AppId appId, DepotId depotId)
//        {
//            if (_server.InternalServer.Type == "CDN" || _server.InternalServer.Type == "SteamCache")
//            {
//                _authTokens.TryGetValue((appId, depotId), out var authToken);

//                // We have a token, but it's invalid?
//                if (authToken != null && authToken.Expires - DateTime.UtcNow < TimeSpan.FromMinutes(5))
//                {
//                    // Remove the now invalid auth token
//                    _authTokens.TryRemove((appId, depotId), out var _);
//                }

//                // We dont have an auth token? Request one
//                if (authToken == null)
//                {
//                    var result = await _steamContentClient.SteamApps.GetCDNAuthToken(appId, depotId, _server.InternalServer.Host);
//                    authToken = new CdnClientAuthToken(result.Token, result.Expiration, appId, depotId);

//                    // Give the data to the underlying CDN client too
//                    InternalCdnClient.AuthenticateDepot(depotId, await GetDepotKeyAsync(depotId, appId), authToken.Token);

//                    _authTokens.AddOrUpdate((appId, depotId), authToken, (key, currentValue) => authToken);
//                }
//            }
//        }

//        private class CdnClientAuthToken
//        {
//            public CdnClientAuthToken(string authToken, DateTime expires, AppId appId, DepotId depotId)
//            {
//                Token = authToken;
//                Expires = expires;
//                AppId = appId;
//                DepotId = depotId;
//            }

//            public string Token { get; }
//            public DateTime Expires { get; }
//            public AppId AppId { get; }
//            public DepotId DepotId { get; }
//        }
//    }
//}
