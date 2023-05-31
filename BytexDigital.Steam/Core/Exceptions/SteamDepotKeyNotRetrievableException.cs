using BytexDigital.Steam.Core.Structs;
using SteamKit2;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDepotKeyNotRetrievableException : SteamAccessDeniedException
    {
        public SteamDepotKeyNotRetrievableException(DepotId depotId, EResult result) : base(
            $"Cannot get decryption key for depot {depotId}: {result}")
        {
        }
    }
}