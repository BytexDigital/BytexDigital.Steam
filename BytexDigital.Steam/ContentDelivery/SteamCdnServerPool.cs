using BytexDigital.Steam.ContentDelivery.Models;

using Nito.AsyncEx;

using SteamKit2;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery
{
    public class SteamCdnServerPool
    {
        private SteamApps _steamApps;
        private SteamContentClient _steamContentClient;
        private SteamContentServerQualityProvider _steamContentServerQualityProvider;
        private IList<SteamContentServerQuality> _steamContentServerQualities;
        private Dictionary<SteamCdnClient, int> _cdnClients = new Dictionary<SteamCdnClient, int>();

        private readonly AsyncSemaphore _getClientSemaphore = new AsyncSemaphore(1);

        public SteamCdnServerPool(SteamContentClient steamContentClient, SteamContentServerQualityProvider steamContentServerQualityProvider)
        {
            _steamApps = steamContentClient.SteamClient.InternalClient.GetHandler<SteamKit2.SteamApps>();
            _steamContentClient = steamContentClient;
            _steamContentServerQualityProvider = steamContentServerQualityProvider;

            _steamContentServerQualities = _steamContentServerQualityProvider.Load() ?? new List<SteamContentServerQuality>();
        }

        public async Task<SteamCdnClient> GetClientAsync(CancellationToken cancellationToken = default)
        {
            await _getClientSemaphore.WaitAsync(cancellationToken);

            if (_cdnClients.Count == 0)
            {
                await FillPoolAsync();
            }

            // Select client that has been handed out the least yet
            var cdnClient = _cdnClients
                .OrderBy(x => x.Key.Errors)
                .ThenBy(x => x.Value)
                .First()
                .Key;

            _cdnClients[cdnClient]++;

            _getClientSemaphore.Release();

            return cdnClient;
        }

        public void NotifyClientError(SteamCdnClient steamCdnClient)
        {
            lock (steamCdnClient)
            {
                steamCdnClient.Errors++;
            }
        }

        public async Task FillPoolAsync()
        {
            // Await until we've logged in and have our suggest cell ID
            await _steamContentClient.SteamClient.AwaitReadyAsync(_steamContentClient.SteamClient.CancellationToken);

            // Get servers
            var servers = await SteamKit2.ContentServerDirectoryService.LoadAsync(
                _steamContentClient.SteamClient.InternalClient.Configuration,
                (int)_steamContentClient.SteamClient.SuggestedCellId,
                _steamContentClient.CancellationToken);

            foreach (var server in servers)
            {
                _cdnClients.Add(new SteamCdnClient(_steamContentClient, new SteamCdnServer(server)), 0);
            }
        }
    }
}
