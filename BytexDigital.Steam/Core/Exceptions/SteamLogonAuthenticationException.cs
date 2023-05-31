using System;

namespace BytexDigital.Steam.Core.Exceptions
{
    public class SteamLogonAuthenticationException : Exception
    {
        public SteamLogonAuthenticationException(Exception exception) : base(
            "Could not perform login attempt. See inner exception if present.",
            exception)
        {
        }
    }
}