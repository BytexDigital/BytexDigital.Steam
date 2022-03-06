using System;
using System.Collections.Generic;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamOsNotSupportedByAppException : Exception
    {
        public SteamOsNotSupportedByAppException(AppId appId, DepotId depotId, ManifestId manifestId, SteamOs targetOs,
            IEnumerable<string> availableOs)
            : base(
                $"App id = {appId}, depot id = {depotId}, manifest id = {manifestId} only supports OS {string.Join(", ", availableOs)} but not {targetOs}")
        {
        }
    }
}