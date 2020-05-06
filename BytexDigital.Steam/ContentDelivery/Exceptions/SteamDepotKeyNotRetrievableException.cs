using BytexDigital.Steam.Core.Structs;
using SteamKit2;
using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDepotKeyNotRetrievableException : Exception
    {
        public SteamDepotKeyNotRetrievableException(DepotId depotId, EResult result) : base($"Cannot get decryption key for depot {depotId}: {result}")
        {
        }
    }
}
