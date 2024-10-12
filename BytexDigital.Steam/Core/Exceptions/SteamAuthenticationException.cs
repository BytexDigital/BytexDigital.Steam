using System;
using SteamKit2;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamAuthenticationException : Exception
    {
        public EResult Result { get; }
        public SteamAuthenticationException(EResult result) : base($"Error code: {result}") => Result = result;
    }
}
