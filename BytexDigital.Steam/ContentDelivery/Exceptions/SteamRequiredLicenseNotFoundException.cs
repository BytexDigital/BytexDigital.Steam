using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamRequiredLicenseNotFoundException : SteamAccessDeniedException
    {
        public SteamRequiredLicenseNotFoundException(AppId appId) : base(
            "The Steam user cannot access this content as it does not own a " +
            $"license for app id {appId} and no free one could be optained either.")
        {
        }
    }
}