using System;
using SteamKit2;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamLogonException : Exception
    {
        public EResult Result { get; }
        public SteamLogonException(EResult result) : base($"Error code: {result}") => Result = result;
    }
}
