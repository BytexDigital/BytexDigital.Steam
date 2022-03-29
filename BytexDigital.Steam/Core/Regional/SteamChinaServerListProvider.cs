using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Discovery;

namespace BytexDigital.Steam.Core.Regional
{
    public class SteamChinaServerListProvider : IRegionSpecificServerListProvider
    {
        public SteamConfiguration SteamConfiguration { get; private set; }

        public async Task<IEnumerable<ServerRecord>> FetchServerListAsync()
        {
            if (SteamConfiguration == null)
            {
                throw new InvalidOperationException(
                    "Cannot fetch server list without first providing a SteamConfiguration object.");
            }

            var directory = SteamConfiguration.GetAsyncWebAPIInterface("ISteamDirectory");
            var args = new Dictionary<string, object?>
            {
                ["cellid"] = SteamConfiguration.CellID.ToString(CultureInfo.InvariantCulture),
                ["launcher"] = "7",
                ["steamrealm"] = "steamchina"
            };

            var response = await directory.CallAsync(HttpMethod.Get, "GetCMList", 1, args).ConfigureAwait(false);

            var result = (EResult) response["result"].AsInteger((int) EResult.Invalid);
            if (result != EResult.OK)
            {
                throw new InvalidOperationException($"Steam Web API returned EResult.{result}");
            }

            var socketList = response["serverlist"];
            var websocketList = response["serverlist_websockets"];

            var serverRecords = new List<ServerRecord>(socketList.Children.Count + websocketList.Children.Count);

            foreach (var child in socketList.Children)
            {
                if (child.Value is null || !ServerRecord.TryCreateSocketServer(child.Value, out var record))
                {
                    continue;
                }

                serverRecords.Add(record);
            }

            foreach (var child in websocketList.Children)
            {
                if (child.Value is null)
                {
                    continue;
                }

                serverRecords.Add(ServerRecord.CreateWebSocketServer(child.Value));
            }

            return serverRecords.AsReadOnly();
        }

        public Task UpdateServerListAsync(IEnumerable<ServerRecord> endpoints)
        {
            return Task.CompletedTask;
        }

        public void SetSteamConfiguration(SteamConfiguration steamConfiguration)
        {
            SteamConfiguration = steamConfiguration;
        }
    }
}