using BytexDigital.Steam.Core.Structs;

using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamInvalidBranchPasswordException : Exception
    {
        public SteamInvalidBranchPasswordException(AppId appId, DepotId depotId, string branch, string password, Exception innerException = null) : base($"Invalid password = " +
            $"'{password}' for branch = {branch} of app id = {appId}, depot id = {depotId}{(innerException != null ? ". View the inner exception for more details." : "")}", innerException)
        {
        }
    }
}
