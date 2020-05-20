using System;
using System.Collections.Generic;
using System.Text;

namespace BytexDigital.Steam.Core
{
    public abstract class SteamAuthenticationCodesProvider
    {
        public abstract string GetTwoFactorAuthenticationCode(SteamCredentials steamCredentials);
        public abstract string GetEmailAuthenticationCode(SteamCredentials steamCredentials);
    }

    public class DefaultSteamAuthenticationCodesProvider : SteamAuthenticationCodesProvider
    {
        public override string GetEmailAuthenticationCode(SteamCredentials steamCredentials) => null;

        public override string GetTwoFactorAuthenticationCode(SteamCredentials steamCredentials) => null;
    }
}
