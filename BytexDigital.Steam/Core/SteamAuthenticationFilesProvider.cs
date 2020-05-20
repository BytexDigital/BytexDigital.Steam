using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace BytexDigital.Steam.Core
{
    public abstract class SteamAuthenticationFilesProvider
    {
        public abstract byte[] GetSentryFileContent(SteamCredentials steamCredentials);
        public abstract void SaveSentryFileContent(SteamCredentials steamCredentials, byte[] data);

        public abstract string? GetLoginKey(SteamCredentials steamCredentials);
        public abstract void SaveLoginKey(SteamCredentials steamCredentials, string loginKey);
    }

    public class DefaultSteamAuthenticationFilesProvider : SteamAuthenticationFilesProvider
    {
        private static ConcurrentDictionary<string, byte[]> _sentryFileData = new ConcurrentDictionary<string, byte[]>();
        private static ConcurrentDictionary<string, string> _loginKeys = new ConcurrentDictionary<string, string>();

        public override string GetLoginKey(SteamCredentials steamCredentials)
        {
            return _loginKeys.GetValueOrDefault(steamCredentials.Username, null);
        }

        public override byte[] GetSentryFileContent(SteamCredentials steamCredentials)
        {
            return _sentryFileData.GetValueOrDefault(steamCredentials.Username, null);
        }

        public override void SaveLoginKey(SteamCredentials steamCredentials, string loginKey)
        {
            _loginKeys.AddOrUpdate(steamCredentials.Username, key => loginKey, (key, existingValue) => loginKey);
        }

        public override void SaveSentryFileContent(SteamCredentials steamCredentials, byte[] data)
        {
            _sentryFileData.AddOrUpdate(steamCredentials.Username, key => data, (key, existingValue) => data);
        }
    }

    public class DirectorySteamAuthenticationFilesProvider : SteamAuthenticationFilesProvider
    {
        private readonly string _directory;

        public DirectorySteamAuthenticationFilesProvider(string directory)
        {
            _directory = directory;
        }

        public override string GetLoginKey(SteamCredentials steamCredentials)
        {
            string file = Path.Combine(_directory, $"sentry_{steamCredentials.Username}.key");

            if (!File.Exists(file))
            {
                return null;
            }
            else
            {
                return File.ReadAllText(file);
            }
        }

        public override byte[] GetSentryFileContent(SteamCredentials steamCredentials)
        {
            string file = Path.Combine(_directory, $"sentry_{steamCredentials.Username}.bin");

            if (!File.Exists(file))
            {
                return null;
            }
            else
            {
                return File.ReadAllBytes(file);
            }
        }

        public override void SaveLoginKey(SteamCredentials steamCredentials, string loginKey)
        {
            string file = Path.Combine(_directory, $"sentry_{steamCredentials.Username}.key");

            Directory.CreateDirectory(_directory);
            File.WriteAllText(file, loginKey);
        }

        public override void SaveSentryFileContent(SteamCredentials steamCredentials, byte[] data)
        {
            string file = Path.Combine(_directory, $"sentry_{steamCredentials.Username}.bin");

            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(file, data);
        }
    }
}
