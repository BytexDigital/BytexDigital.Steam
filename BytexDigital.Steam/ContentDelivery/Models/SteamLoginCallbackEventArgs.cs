using SteamKit2;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class SteamLoginCallbackEventArgs
    {
        private readonly EResult _result;

        public bool Requires2FA => _result == EResult.AccountLoginDeniedNeedTwoFactor;
        public bool RequiresSteamGuardCode => _result == EResult.AccountLogonDenied;

        public SteamLoginCallbackEventArgs(EResult result)
        {
            _result = result;
        }
    }
}
