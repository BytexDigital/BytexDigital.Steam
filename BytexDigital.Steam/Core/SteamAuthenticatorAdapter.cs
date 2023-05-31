using System.Threading.Tasks;
using SteamKit2.Authentication;

namespace BytexDigital.Steam.Core
{
    public class SteamAuthenticatorAdapter : IAuthenticator
    {
        private readonly SteamAuthenticator _publicAuthenticator;

        public SteamAuthenticatorAdapter(SteamAuthenticator publicAuthenticator)
        {
            _publicAuthenticator = publicAuthenticator;
        }

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            return await _publicAuthenticator.GetTwoFactorAuthenticationCodeAsync(previousCodeWasIncorrect);
        }

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            return await _publicAuthenticator.GetEmailAuthenticationCodeAsync(email, previousCodeWasIncorrect);
        }

        public async Task<bool> AcceptDeviceConfirmationAsync()
        {
            return await _publicAuthenticator.NotifyMobileNotificationAsync();
        }
    }
}