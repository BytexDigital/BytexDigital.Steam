using BytexDigital.Steam.Core.Structs;

using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamNoContentServerFoundException : Exception
    {
        public SteamNoContentServerFoundException(AppId appId) : base($"No content servers could be found for app id = {appId}.")
        {
        }
    }
}
