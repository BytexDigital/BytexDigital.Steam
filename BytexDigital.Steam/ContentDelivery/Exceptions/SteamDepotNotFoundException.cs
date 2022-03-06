using System;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDepotNotFoundException : Exception
    {
        public SteamDepotNotFoundException(DepotId depotId) : base($"Depot {depotId} not found.")
        {
        }
    }
}
