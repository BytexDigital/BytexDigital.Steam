using SteamKit2;
using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamPublishedFileDetailsFetchException : Exception
    {
        public SteamPublishedFileDetailsFetchException(EResult result) : base($"Could not download published file details: {result}")
        {
        }
    }
}
