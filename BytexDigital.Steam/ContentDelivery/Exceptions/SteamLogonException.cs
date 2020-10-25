using SteamKit2;

using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamLogonException : Exception
    {
        public SteamLogonException(EResult result) : base($"Error code: {result}")
        {
            Result = result;
        }

        public EResult Result { get; }
    }
}
