using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.Core.Structs;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using SteamKit2;
using SteamKit2.CDN;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamCdnServerPool : IDisposable
    {
        private const int MinimumPoolSize = 10;
        private readonly ConcurrentStack<Server> _activeServerEndpoints;
        private readonly AppId _appId;
        private readonly BlockingCollection<Server> _availableServerEndpoints;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly AsyncManualResetEvent _populatedEvent;
        private readonly AutoResetEvent _populatePoolEvent;

        private readonly Task _populatorTask;
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
            _activeServerEndpoints = new ConcurrentStack<Server>();
            _populatePoolEvent = new AutoResetEvent(true);
            _populatedEvent = new AsyncManualResetEvent(false);
            _populatorTask = Task.Factory.StartNew(MonitorAsync).Unwrap();

            CdnClient = new Client(_steamContentClient.SteamClient.InternalClient);
        }

        public void Dispose()
        {
            Close();
        }

        public Server GetServer(CancellationToken cancellationToken = default)
        {
            Server server;

            do
            {
                if (!_activeServerEndpoints.TryPop(out server))
                {
                    if (_availableServerEndpoints.Count < MinimumPoolSize && !_populatedEvent.IsSet)
                    {
                        Logger?.LogTrace("GetServerAsync: Set populatePoolEvent");
                        _populatePoolEvent.Set();

                        //Logger?.LogTrace("GetServerAsync: Waiting for pool to be populated");
                        //await _populatedEvent.WaitAsync(cancellationToken);
                    }

                    server = _availableServerEndpoints.Take(cancellationToken);
                }
            } while (server == null);

            return server;
        }

        public void ReturnServer(Server server, bool isFaulty)
        {
            if (isFaulty)
            {
                return;
            }

            _activeServerEndpoints.Push(server);
        }

        private async Task MonitorAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                Logger?.LogTrace("MonitorAsync: Waiting for a few seconds before checking if pool needs population");
                _populatePoolEvent.WaitOne(TimeSpan.FromSeconds(5));

                Logger?.LogTrace("MonitorAsync: Checking");
                if (_availableServerEndpoints.Count < MinimumPoolSize)
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
    }
}