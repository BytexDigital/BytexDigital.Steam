using System;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamBranchNotFoundException : Exception
    {
        public SteamBranchNotFoundException(AppId appId, DepotId depotId, string branch) : base(
            $"Branch {branch} could not be found for app id = {appId}, depot id = {depotId}")
        {
        }
    }
}