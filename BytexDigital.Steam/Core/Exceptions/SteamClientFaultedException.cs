using BytexDigital.Steam.Core.Enumerations;

using System;

namespace BytexDigital.Steam.Core.Exceptions
{
    public class SteamClientFaultedException : Exception
    {
        public SteamClientFaultedException(SteamClientFaultReason reason) : base($"Client is faulted and cannot be used: {reason}")
        {
        }
    }
}
