using System;

namespace BytexDigital.Steam.Core.Exceptions
{
    public class SteamClientNotReadyException : Exception
    {
        public SteamClientNotReadyException() : base("The SteamClient is not ready for operational use.")
        {
        }
    }
}
