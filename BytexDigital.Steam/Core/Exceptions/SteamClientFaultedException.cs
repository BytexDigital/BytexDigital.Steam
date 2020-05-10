using BytexDigital.Steam.Core.Enumerations;

using System;

namespace BytexDigital.Steam.Core.Exceptions
{
    public class SteamClientFaultedException : Exception
    {
        public SteamClientFaultedException(Exception faultException) : base($"Client is faulted and cannot be used{(faultException != null ? ": " : ".")}{faultException?.Message}", faultException)
        {
        }
    }
}
