using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.Core.Structs;

using Nito.AsyncEx;

using SteamKit2;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamCdnClientPool
    {
        private readonly ConcurrentDictionary<uint, byte[]> _depotKeys = new ConcurrentDictionary<uint, byte[]>();
        private readonly SteamContentClient _steamContentClient;
        private readonly SteamContentServerQualityProvider _steamContentServerQualityProvider;
        private readonly SteamKit2.SteamApps _steamApps;
        private readonly ConcurrentBag<CdnServerWrapper> _servers = new ConcurrentBag<CdnServerWrapper>();
        private readonly List<CdnClientWrapper> _cdnClientWrappersAvailable = new List<CdnClientWrapper>();
        private readonly AsyncManualResetEvent _poolFilledEvent = new AsyncManualResetEvent(false);
        private static Random _random = new Random();
        private readonly IList<SteamContentServerQuality> _steamContentServerQualities;
        private readonly AsyncSemaphore _getClientSemaphore = new AsyncSemaphore(1);

        public const int CLIENTS_REFILL_LIMIT = 20;

        public SteamCdnClientPool(SteamContentClient steamContentClient, SteamContentServerQualityProvider steamContentServerQualityProvider)
        {
            _steamApps = steamContentClient.SteamClient.InternalClient.GetHandler<SteamKit2.SteamApps>();
            _steamContentClient = steamContentClient;
            _steamContentServerQualityProvider = steamContentServerQualityProvider;

            _steamContentServerQualities = _steamContentServerQualityProvider.Load() ?? new List<SteamContentServerQuality>();
        }

        public async Task<CdnClientWrapper> GetClient(AppId appId, DepotId depotId, CancellationToken cancellationToken = default)
        {
            await _getClientSemaphore.WaitAsync(cancellationToken);

            if (_cdnClientWrappersAvailable.Count == 0)
            {
                await FillServerPoolAsync();
            }

            await _poolFilledEvent.WaitAsync(cancellationToken);

            CdnClientWrapper recommendedClient = null;

            lock (this)
            {
                recommendedClient = _cdnClientWrappersAvailable
                    .OrderByDescending(x => x.ServerWrapper.Score)
                    .ThenBy(x => x.ServerWrapper.Server.WeightedLoad)
                    .ThenBy(x => x.ServerWrapper.Server.Load)
                    .Take(10)
                    .OrderBy(x => _random.Next())
                    .First();

                _cdnClientWrappersAvailable.Remove(recommendedClient);
            }

            if (recommendedClient.RequiresAuthentication(appId, depotId))
            {
                await AuthenticateClientInfoAsync(recommendedClient, appId, depotId);
            }

            _getClientSemaphore.Release();

            return recommendedClient;
        }

        public void ReturnClient(CdnClientWrapper cdnClientWrapper, bool isFunctional = true)
        {
            if (cdnClientWrapper == null) return;

            lock (this)
            {
                if (isFunctional)
                {
                    cdnClientWrapper.ServerWrapper.Score += 0.1;

                    _cdnClientWrappersAvailable.Add(cdnClientWrapper);
                }
                else
                {
                    cdnClientWrapper.ServerWrapper.Score -= 1;
                }
            }

            SaveServerQualitiesMemory();
        }

        private void SaveServerQualitiesMemory()
        {
            lock (_steamContentServerQualities)
            {
                var allKnownServers = _servers;

                foreach (var serverWrapper in allKnownServers)
                {
                    var serverMemory = _steamContentServerQualities.FirstOrDefault(x => x.Host == serverWrapper.Server.Host);

                    if (serverMemory != null)
                    {
                        serverMemory.Score = serverWrapper.Score;
                    }
                    else
                    {
                        _steamContentServerQualities.Add(new SteamContentServerQuality
                        {
                            Host = serverWrapper.Server.Host,
                            Score = serverWrapper.Score
                        });
                    }
                }

                _steamContentServerQualityProvider.Save(_steamContentServerQualities);
            }
        }

        private async Task AuthenticateClientInfoAsync(CdnClientWrapper client, AppId appId, DepotId depotId)
        {
            if (client.ServerWrapper.Server.Type == "CDN" || client.ServerWrapper.Server.Type == "SteamCache")
            {
                var authToken = client.GetAuthTokenOrDefault(appId, depotId);

                if (authToken == null)
                {
                    var result = await _steamApps.GetCDNAuthToken(appId, depotId, client.ServerWrapper.Server.Host);

                    authToken = new CdnClientAuthToken(result.Token, result.Expiration, appId, depotId);

                    client.AuthTokens.Add(authToken);
                }

                await client.CdnClient.AuthenticateDepotAsync(depotId, await GetDepotKeyAsync(depotId, appId), authToken.Token);
            }
        }

        public async Task<byte[]> GetDepotKeyAsync(DepotId depotId, AppId appId)
        {
            if (_depotKeys.ContainsKey(depotId))
            {
                return _depotKeys[depotId];
            }

            var result = await _steamApps.GetDepotDecryptionKey(depotId, appId);

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

        public async Task FillServerPoolAsync()
        {
            // Await until we've logged in and have our suggest cell ID
            await _steamContentClient.SteamClient.AwaitReadyAsync(_steamContentClient.SteamClient.CancellationToken);

            // Add any not-yet-tracked servers
            var servers = await GetServersAsync();

            foreach (var server in servers)
            {
                if (_servers.Any(x => x.Server.Host == server.Host)) continue;

                var serverWrapper = new CdnServerWrapper(server);
                var qualityFromMemory = _steamContentServerQualities.FirstOrDefault(x => x.Host == serverWrapper.Server.Host);

                if (qualityFromMemory != null)
                {
                    serverWrapper.Score = qualityFromMemory.Score;
                }

                _servers.Add(serverWrapper);
            }


            // Fresh database over server quality
            if (_servers.Sum(x => x.Score) == 0)
            {
                foreach (var serverWrapper in _servers)
                {
                    for (int i = 0; i < serverWrapper.Server.NumEntries; i++)
                    {
                        var cdnClient = new CDNClient(_steamContentClient.SteamClient.InternalClient);

                        await cdnClient.ConnectAsync(serverWrapper.Server);

                        var wrapper = new CdnClientWrapper(cdnClient, serverWrapper);

                        _cdnClientWrappersAvailable.Add(wrapper);
                    }
                }
            }
            else
            {
                var orderedServers = new Queue<CdnServerWrapper>(_servers.OrderByDescending(x => x.Score).OrderBy(x => x.Server.WeightedLoad).OrderBy(x => x.Server.Load));
                int refillsRemaining = CLIENTS_REFILL_LIMIT;

                while (refillsRemaining > 0)
                {
                    var serverWrapper = orderedServers.Dequeue();

                    for (int i = 0; i < serverWrapper.Server.NumEntries; i++)
                    {
                        var cdnClient = new CDNClient(_steamContentClient.SteamClient.InternalClient);
                        var wrapper = new CdnClientWrapper(cdnClient, serverWrapper);

                        _cdnClientWrappersAvailable.Add(wrapper);

                        if (--refillsRemaining <= 0) break;
                    }
                }
            }

            _poolFilledEvent.Set();
        }

        private async Task<IEnumerable<SteamKit2.CDNClient.Server>> GetServersAsync()
        {
            return await SteamKit2.ContentServerDirectoryService.LoadAsync(
                _steamContentClient.SteamClient.InternalClient.Configuration,
                (int)_steamContentClient.SteamClient.SuggestedCellId,
                _steamContentClient.CancellationToken);
        }

        public class CdnClientWrapper
        {
            public CdnClientWrapper(SteamKit2.CDNClient cdnClient, CdnServerWrapper serverWrapper)
            {
                CdnClient = cdnClient;
                ServerWrapper = serverWrapper;
            }

            public CDNClient CdnClient { get; }
            public CdnServerWrapper ServerWrapper { get; }
            public List<CdnClientAuthToken> AuthTokens { get; private set; } = new List<CdnClientAuthToken>();

            public CdnClientAuthToken GetAuthTokenOrDefault(AppId appId, DepotId depotId)
            {
                return AuthTokens.FirstOrDefault(x => x.AppId == appId && x.DepotId == depotId);
            }

            public bool RequiresAuthentication(AppId appId, DepotId depotId)
            {
                var authToken = GetAuthTokenOrDefault(appId, depotId);

                if (authToken == null) return true;

                return authToken.Expires - DateTime.UtcNow < TimeSpan.FromMinutes(5);
            }
        }

        public class CdnServerWrapper
        {
            public CdnServerWrapper(CDNClient.Server server)
            {
                Server = server;
            }

            public double Score { get; set; } = 0;
            public CDNClient.Server Server { get; }
        }

        public class CdnClientAuthToken
        {
            public CdnClientAuthToken(string authToken, DateTime expires, AppId appId, DepotId depotId)
            {
                Token = authToken;
                Expires = expires;
                AppId = appId;
                DepotId = depotId;
            }

            public string Token { get; }
            public DateTime Expires { get; }
            public AppId AppId { get; }
            public DepotId DepotId { get; }
        }
    }
}
