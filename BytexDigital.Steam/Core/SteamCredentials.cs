using System;

namespace BytexDigital.Steam.Core
{
    public class SteamCredentials
    {
        private const string ANONYMOUS_USERNAME = "";
        public static readonly SteamCredentials Anonymous = new SteamCredentials(ANONYMOUS_USERNAME, "");

        public string Username { get; }
        public string Password { get; }
        public string WebApiKey { get; }

        public bool IsAnonymous => Username == ANONYMOUS_USERNAME;

        public SteamCredentials(string username, string password, string webApiKey = null)
        {
            if (username != ANONYMOUS_USERNAME)
            {
                if (string.IsNullOrEmpty(username))
                {
                    throw new ArgumentException($"\"{nameof(username)}\" may not be NULL or empty.", nameof(username));
                }

                if (string.IsNullOrEmpty(password))
                {
                    throw new ArgumentException($"\"{nameof(password)}\" may not be NULL or empty.", nameof(password));
                }
            }

            Username = username;
            Password = password;
            WebApiKey = webApiKey;
        }
    }
}