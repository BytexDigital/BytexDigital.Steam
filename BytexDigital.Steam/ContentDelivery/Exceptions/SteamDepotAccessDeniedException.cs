using BytexDigital.Steam.Core.Structs;

using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDepotAccessDeniedException : Exception
    {
        public SteamDepotAccessDeniedException(DepotId depotId) : base($"Access to depot id = {depotId} denied.")
        {
        }
    }
}
