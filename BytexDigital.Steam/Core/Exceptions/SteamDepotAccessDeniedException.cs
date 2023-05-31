using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDepotAccessDeniedException : SteamAccessDeniedException
    {
        public SteamDepotAccessDeniedException(DepotId depotId) : base($"Access to depot id = {depotId} denied.")
        {
        }
    }
}