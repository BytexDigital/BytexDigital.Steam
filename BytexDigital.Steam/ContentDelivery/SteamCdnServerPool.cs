using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Structs;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using SteamKit2;
using SteamKit2.CDN;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamCdnServerPool : IDisposable
    {
        private const int MINIMUM_POOL_SIZE = 10;
        private readonly AppId _appId;
        private readonly BlockingCollection<Server> _availableServerEndpoints;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly AsyncManualResetEvent _populatedEvent;
        private readonly AutoResetEvent _populatePoolEvent;

        private readonly Task _populatorTask;

        private readonly List<ServerWithRating> _ratedServers;
        private readonly Task _ratingsAnalyzerTask;
        private readonly AsyncLock _serverLock;
        private readonly ConcurrentDictionary<Server, List<int>> _serverRatingsHistory;
        private SteamContentClient _steamContentClient;

        public ILogger Logger { get; set; }
        public Client CdnClient { get; }
        public Server DesignatedProxyServer { get; private set; }
        public bool IsExhausted { get; private set; }

        public SteamCdnServerPool(
            SteamContentClient steamContentClient,
            AppId appId,
            CancellationToken cancellationToken = default)
        {
            _steamContentClient = steamContentClient;
            _appId = appId;
            _cancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource().Token);
            _availableServerEndpoints = new BlockingCollection<Server>();
            //_activeServerEndpoints = new ConcurrentStack<Server>();
            _serverRatingsHistory = new ConcurrentDictionary<Server, List<int>>();
            _ratedServers = new List<ServerWithRating>();
            _populatePoolEvent = new AutoResetEvent(true);
            _populatedEvent = new AsyncManualResetEvent(false);
            _populatorTask = Task.Factory.StartNew(MonitorAsync).Unwrap();
            _ratingsAnalyzerTask = Task.Factory.StartNew(AnalyzeRatingsAsync).Unwrap();
            _serverLock = new AsyncLock();

            CdnClient = new Client(_steamContentClient.SteamClient.InternalClient);
        }

        public void Dispose()
        {
            Close();
        }

        public async Task<Server> GetServerAsync(CancellationToken cancellationToken = default)
        {
            Logger?.LogTrace("GetServerAsync: Called");

            Server server = default;

            if (IsExhausted)
            {
                throw new SteamNoContentServerFoundException(_appId);
            }

            while (server == null)
            {
                if (_ratedServers.Count > 0)
                {
                    using var lk = await _serverLock.LockAsync(cancellationToken);

                    var random = new Random();

                    var serverWithRating = _ratedServers
                        .OrderBy(x => x.Rating)
                        .Take(10)
                        .OrderBy(x => random.Next())
                        .FirstOrDefault();

                    if (serverWithRating != null)
                    {
                        server = serverWithRating.Server;

                        continue;
                    }
                }

                if (_availableServerEndpoints.Count < MINIMUM_POOL_SIZE)
                {
                    Logger?.LogTrace("GetServerAsync: Set populatePoolEvent");
                    _populatePoolEvent.Set();

                    Logger?.LogTrace("GetServerAsync: Waiting for pool to be populated");
                    await _populatedEvent.WaitAsync(cancellationToken);
                }

                Logger?.LogTrace("GetServerAsync: Trying to pop..");
                _availableServerEndpoints.TryTake(out server);
            }

            return server;
        }

        public async void ReturnServer(Server server, bool isFaulty, int rating = 0)
        {
            if (isFaulty)
            {
                using var lk = await _serverLock.LockAsync().ConfigureAwait(false);

                if (_ratedServers.Any(x => x.Server == server))
                {
                    _ratedServers.Remove(_ratedServers.First(x => x.Server == server));
                }

                return;
            }

            //_activeServerEndpoints.Push(server);

            if (rating != 0)
            {
                using var lk = await _serverLock.LockAsync().ConfigureAwait(false);

                var ratings = _serverRatingsHistory.GetOrAdd(server, x => new List<int>());

                ratings.Add(rating);

                if (!_ratedServers.Any(x => x.Server == server))
                {
                    _ratedServers.Add(
                        new ServerWithRating
                        {
                            Server = server,
                            Rating = rating
                        });
                }
            }
        }

        public Task InvalidateServerAuthenticationAsync(string authenticationKey)
        {
            _steamContentClient.CdnAuthenticationTokens.TryRemove(authenticationKey, out _);

            return Task.CompletedTask;
        }

        public async Task<string> AuthenticateWithServerAsync(DepotId depotId, Server server)
        {
            var host = server.Host;

            if (host.EndsWith(".steampipe.steamcontent.com"))
            {
                host = "steampipe.steamcontent.com";
            }
            else if (host.EndsWith(".steamcontent.com"))
            {
                host = "steamcontent.com";
            }

            var cdnKey = $"{depotId.Id:D}:{host}";

            return await GetCdnAuthenticationTokenAsync(_appId, depotId, host, cdnKey);
        }

        private async Task<string> GetCdnAuthenticationTokenAsync(
            AppId appId,
            DepotId depotId,
            string host,
            string cdnKey)
        {
            Logger?.LogTrace("GetCdnAuthenticationTokenAsync: Calling TryGetValue");

            if (_steamContentClient.CdnAuthenticationTokens.TryGetValue(cdnKey, out var response))
            {
                Logger?.LogTrace("GetCdnAuthenticationTokenAsync: Got existing token");

                // Check if the token has expired, ignore expiration dates that are UnixEpoch, probably an error,
                // they work regardless..
                if (response.Expiration != DateTime.UnixEpoch &&
                    response.Expiration - DateTime.UtcNow < TimeSpan.FromMinutes(1))
                {
                    Logger?.LogTrace("GetCdnAuthenticationTokenAsync: Existing token was expired");

                    // Remove from our store and just fetch a new auth token
                    _steamContentClient.CdnAuthenticationTokens.TryRemove(cdnKey, out _);
                }
                else
                {
                    Logger?.LogTrace("GetCdnAuthenticationTokenAsync: Existing token was valid");

                    return response.Token;
                }
            }

            Logger?.LogTrace("GetCdnAuthenticationTokenAsync: Calling GetCDNAuthToken");
            var authResponse = await _steamContentClient.SteamApps.GetCDNAuthToken(appId, depotId, host);

            Logger?.LogTrace("GetCdnAuthenticationTokenAsync: Adding new token token dictionary");
            _steamContentClient.CdnAuthenticationTokens.AddOrUpdate(
                cdnKey,
                authResponse,
                (key, existingValue) => authResponse);

            return authResponse.Token;
        }

        private async Task MonitorAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                Logger?.LogTrace("MonitorAsync: Waiting for a few seconds before checking if pool needs population");
                _populatePoolEvent.WaitOne(TimeSpan.FromSeconds(5));

                Logger?.LogTrace("MonitorAsync: Checking");
                if (_availableServerEndpoints.Count < MINIMUM_POOL_SIZE)
                {
                    Logger?.LogTrace("MonitorAsync: Pool will be populated");
                    _populatedEvent.Reset();

                    Logger?.LogTrace("MonitorAsync: Getting server list");
                    var servers = await GetServerListAsync();

                    Logger?.LogTrace("MonitorAsync: Got server list");

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
                        .Where(
                            server =>
                            {
                                var isContentServer = server.Type == "SteamCache" || server.Type == "CDN";
                                var allowsAppId = server.AllowedAppIds.Length == 0 ||
                                                  server.AllowedAppIds.Contains(_appId);

                                return isContentServer && allowsAppId;
                            })
                        .OrderBy(x => x.WeightedLoad);

                    foreach (var server in sortedServers)
                    {
                        _availableServerEndpoints.Add(server);
                    }
                }

                Logger?.LogTrace("MonitorAsync: Notifying that pool is populated");
                _populatedEvent.Set();
            }
        }

        private async Task AnalyzeRatingsAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                using var lk = await _serverLock.LockAsync().ConfigureAwait(false);

                foreach (var history in _serverRatingsHistory)
                {
                    var serverWithRating = _ratedServers.FirstOrDefault(x => x.Server == history.Key);

                    if (serverWithRating == null)
                    {
                        continue;
                    }

                    if (history.Value.Count < 10)
                    {
                        continue; // Don't calculate an average with less than 10 datapoints
                    }

                    serverWithRating.Rating = (int) history.Value.Average();

                    _serverRatingsHistory[serverWithRating.Server].Clear();
                }

                //Console.WriteLine($"Ratings: {string.Join(", ", _ratedServers.Select(x => $"{x.Server.Host}: {x.Rating}"))}");
            }
        }

        private async Task<IReadOnlyCollection<Server>> GetServerListAsync()
        {
            var throttleDelay = 0;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cancellationTokenSource.Token);

                try
                {
                    var cdnServers = await ContentServerDirectoryService.LoadAsync(
                        _steamContentClient.SteamClient.InternalClient.Configuration,
                        (int) _steamContentClient.SteamClient.ActiveCellId,
                        _cancellationTokenSource.Token);

                    if (cdnServers == null)
                    {
                        throw new InvalidOperationException(
                            "ContentServerDirectoryService did not return a list of servers.");
                    }

                    return cdnServers;
                }
                catch (Exception ex)
                {
                    if (ex is SteamKitWebRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                    {
                        throttleDelay += 5;

                        await Task.Delay(TimeSpan.FromSeconds(throttleDelay), _cancellationTokenSource.Token);
                    }
                }
            }

            return new List<Server>();
        }

        public void Close()
        {
            _cancellationTokenSource.Cancel();
            _steamContentClient = null;
        }

        private class ServerWithRating
        {
            public Server Server { get; set; }
            public int Rating { get; set; }
        }
    }
}