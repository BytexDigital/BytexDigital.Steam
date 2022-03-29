using SteamKit2;
using SteamKit2.Discovery;

namespace BytexDigital.Steam.Core.Regional
{
    public interface IRegionSpecificServerListProvider : IServerListProvider
    {
        void SetSteamConfiguration(SteamConfiguration steamConfiguration);
    }
}