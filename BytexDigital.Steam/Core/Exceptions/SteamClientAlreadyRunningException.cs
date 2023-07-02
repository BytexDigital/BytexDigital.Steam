using System;

namespace BytexDigital.Steam.Core.Exceptions
{
    public class SteamClientAlreadyRunningException : Exception
    {
        public SteamClientAlreadyRunningException() : base("Steam client is already running.")
        {
        }
    }
}