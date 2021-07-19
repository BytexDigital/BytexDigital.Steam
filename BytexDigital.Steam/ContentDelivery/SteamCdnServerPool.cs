
using BytexDigital.Steam.ContentDelivery.Exceptions;
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
    public class SteamCdnServerPool : IDisposable
    {
        private readonly SteamContentClient _steamContentClient;
        private readonly AppId _appId;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly BlockingCollection<CDNClient.Server> _availableServerEndpoints;
        private readonly ConcurrentStack<CDNClient.Server> _activeServerEndpoints;
        private readonly AutoResetEvent _populatePoolEvent;
        private readonly AsyncManualResetEvent _populatedEvent;
        private readonly Task _populatorTask;
        private const int MINIMUM_POOL_SIZE = 10;

        public CDNClient CdnClient { get; private set; }
        public CDNClient.Server DesignatedProxyServer { get; private set; }
        public bool IsExhausted { get; private set; }

        public SteamCdnServerPool(SteamContentClient steamContentClient, AppId appId, CancellationToken cancellationToken = default)
        {
            _steamContentClient = steamContentClient;
            _appId = appId;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource().Token);
            _availableServerEndpoints = new BlockingCollection<CDNClient.Server>();
            _activeServerEndpoints = new ConcurrentStack<CDNClient.Server>();
            _populatePoolEvent = new AutoResetEvent(true);
            _populatedEvent = new AsyncManualResetEvent(false);
            _populatorTask = Task.Factory.StartNew(MonitorAsync).Unwrap();

            CdnClient = new CDNClient(_steamContentClient.SteamClient.InternalClient);
        }

        public async Task<CDNClient.Server> GetServerAsync(CancellationToken cancellationToken = default)
        {
            if (!_activeServerEndpoints.TryPop(out var server))
            {
                if (_availableServerEndpoints.Count < MINIMUM_POOL_SIZE)
                {
                    _populatePoolEvent.Set();

                    await _populatedEvent.WaitAsync(cancellationToken);
                }

                if (IsExhausted) throw new SteamNoContentServerFoundException(_appId);

                server = _availableServerEndpoints.Take(cancellationToken);
            }

            return server;
        }

        public void ReturnServer(CDNClient.Server server, bool isFaulty)
        {
            if (isFaulty) return;

            _activeServerEndpoints.Push(server);
        }

        public async Task<string> AuthenticateWithServerAsync(DepotId depotId, CDNClient.Server server)
        {
            string host = server.Host;

            if (host.EndsWith(".steampipe.steamcontent.com"))
            {
                host = "steampipe.steamcontent.com";
            }
            else if (host.EndsWith(".steamcontent.com"))
            {
                host = "steamcontent.com";
            }

            string cdnKey = $"{depotId.Id:D}:{host}";

            return await GetCdnAuthenticationTokenAsync(_appId, depotId, host, cdnKey);
        }

        private async Task<string> GetCdnAuthenticationTokenAsync(AppId appId, DepotId depotId, string host, string cdnKey)
        {
            if (_steamContentClient.CdnAuthenticationTokens.TryGetValue(cdnKey, out var response))
            {
                // Check if the token has expired
                if (response.Expiration - DateTime.Now < TimeSpan.FromMinutes(1))
                {
                    // Remove from our store and just fetch a new auth token
                    _steamContentClient.CdnAuthenticationTokens.TryRemove(cdnKey, out _);
                }
                else
                {
                    return response.Token;
                }
            }

            var authResponse = await _steamContentClient.SteamApps.GetCDNAuthToken(appId, depotId, host);

            _steamContentClient.CdnAuthenticationTokens.AddOrUpdate(cdnKey, authResponse, (key, existingValue) => authResponse);

            return authResponse.Token;
        }

        private async Task MonitorAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                _populatePoolEvent.WaitOne(TimeSpan.FromSeconds(5));

                if (_availableServerEndpoints.Count < MINIMUM_POOL_SIZE)
                {
                    _populatedEvent.Reset();

                    var servers = await GetServerListAsync();

                    if (servers.Count == 0)
                    {
                        IsExhausted = true;
                        _populatedEvent.Set();
                        _cancellationTokenSource.Cancel();

                        return;
                    }

                    DesignatedProxyServer = servers.FirstOrDefault(x => x.UseAsProxy);

                    var sortedServers = servers
                        // Filter out servers that aren't relevant to us or our appid
                        .Where(server =>
                        {
                            var isContentServer = server.Type == "SteamCache" || server.Type == "CDN";
                            var allowsAppId = server.AllowedAppIds == null || server.AllowedAppIds.Contains(_appId);

                            return isContentServer && allowsAppId;
                        })
                        .OrderBy(x => x.WeightedLoad);

                    foreach (var server in sortedServers)
                    {
                        _availableServerEndpoints.Add(server);
                    }

                    _populatedEvent.Set();
                }
            }
        }

        private async Task<IReadOnlyCollection<CDNClient.Server>> GetServerListAsync()
        {
            int throttleDelay = 0;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cancellationTokenSource.Token);

                try
                {
                    var cdnServers = await ContentServerDirectoryService.LoadAsync(
                        _steamContentClient.SteamClient.InternalClient.Configuration,
                        (int)_steamContentClient.SteamClient.SuggestedCellId,
                        _cancellationTokenSource.Token);

                    if (cdnServers == null) continue;

                    return cdnServers;
                }
                catch (Exception ex)
                {
                    if (ex is SteamKitWebRequestException requestEx && requestEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        throttleDelay += 5;

                        await Task.Delay(TimeSpan.FromSeconds(throttleDelay), _cancellationTokenSource.Token);
                    }
                }
            }

            return new List<CDNClient.Server>();
        }

        public void Close()
        {
            _cancellationTokenSource.Cancel();
        }

        public void Dispose()
        {
            Close();
        }
    }
}
