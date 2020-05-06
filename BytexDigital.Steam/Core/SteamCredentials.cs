namespace BytexDigital.Steam.Core
{
    public class SteamCredentials
    {
        public SteamCredentials(string username, string password, string webApiKey = null)
        {
            Username = username;
            Password = password;
            WebApiKey = webApiKey;
        }

        public string Username { get; }
        public string Password { get; }
        public string WebApiKey { get; }
    }
}
