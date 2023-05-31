using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BytexDigital.Steam.Core
{
    public abstract class SteamAuthenticator
    {
        /// <summary>
        ///     Prompts the user to enter their 2FA code shown on their mobile authenticator.
        /// </summary>
        /// <param name="previousCodeWasIncorrect">Indicates if the last entered 2FA code was incorrect.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<string> GetTwoFactorAuthenticationCodeAsync(
            bool previousCodeWasIncorrect,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Prompts the user to enter their 2FA code sent to their email address.
        /// </summary>
        /// <param name="accountEmail">Domain of user's account email.</param>
        /// <param name="previousCodeWasIncorrect">Indicates if the last entered 2FA code was incorrect.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<string> GetEmailAuthenticationCodeAsync(
            string accountEmail,
            bool previousCodeWasIncorrect,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Executed to prompt the user to accept the login on their Steam mobile app. Return true if the user wishes to do
        ///     this or has already accepted. Return false if a 2FA code should be prompted for instead. Decision can only be
        ///     changed by disconnecting the <see cref="SteamClient" /> explicitly and connecting again.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<bool> NotifyMobileNotificationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        ///     Called to persist the successfully received JWT access token.
        /// </summary>
        /// <param name="token">JWT access token.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task PersistAccessTokenAsync(string token, CancellationToken cancellationToken = default);

        /// <summary>
        ///     Called to retrieve the JWT access token for sign in.
        ///     <remarks>Return FALSE if no access token is available and/or re-authentication should happen.</remarks>
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

        /// <summary>
        ///     Called to persist the successfully received guard data for this machine and account.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task PersistGuardDataAsync(string data, CancellationToken cancellationToken = default);

        /// <summary>
        ///     Called to retreive the guard data for this machine and account.
        ///     <remarks>Return FALSE if no access token is available and/or re-authentication should happen.</remarks>
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public abstract Task<string> GetGuardDataAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    ///     Example implementation of a <see cref="SteamAuthenticator" />. You should create your own by inheriting from
    ///     <see cref="SteamAuthenticator" /> directly.
    /// </summary>
    public class ConsoleSteamAuthenticator : SteamAuthenticator
    {
        private readonly string _persistenceDirectory;
        private readonly string _uniqueStorageName;
        public string AccessToken { get; protected set; }
        public string GuardData { get; protected set; }

        public ConsoleSteamAuthenticator(string uniqueStorageName, string persistenceDirectory)
        {
            _uniqueStorageName = uniqueStorageName;
            _persistenceDirectory = persistenceDirectory;
        }

        public override Task<string> GetTwoFactorAuthenticationCodeAsync(
            bool previousCodeWasIncorrect,
            CancellationToken cancellationToken = default)
        {
            if (previousCodeWasIncorrect)
            {
                Console.WriteLine("Previously entered 2FA code was incorrect!");
            }

            string code;

            do
            {
                Console.Write("Please enter your 2FA code:");

                code = Console.ReadLine();
            } while (string.IsNullOrEmpty(code));

            return Task.FromResult(code);
        }

        public override Task<string> GetEmailAuthenticationCodeAsync(
            string accountEmail,
            bool previousCodeWasIncorrect,
            CancellationToken cancellationToken = default)
        {
            if (previousCodeWasIncorrect)
            {
                Console.WriteLine("Previously entered email 2FA code was incorrect!");
            }

            string code;

            do
            {
                Console.Write($"Please enter your 2FA code that was sent to your email ({accountEmail}): ");

                code = Console.ReadLine();
            } while (string.IsNullOrEmpty(code));

            return Task.FromResult(code);
        }

        public override Task<bool> NotifyMobileNotificationAsync(CancellationToken cancellationToken = default)
        {
            Console.Write(
                "Mobile notification sent. Answer \"y\" once you've authorized this login. If no notification was received or you'd like to enter a traditional 2FA code, enter \"n\": ");

            string response;

            do
            {
                response = Console.ReadLine()?.ToLower();
            } while (response != "y" && response != "n");

            return Task.FromResult(response == "y");
        }

        public override Task PersistAccessTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            AccessToken = token;

            if (string.IsNullOrEmpty(_persistenceDirectory)) return Task.CompletedTask;

            Directory.CreateDirectory(_persistenceDirectory);
            File.WriteAllText(Path.Combine(_persistenceDirectory, $"{_uniqueStorageName}_accesstoken"), AccessToken);

            return Task.CompletedTask;
        }

        public override Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_persistenceDirectory))
            {
                return Task.FromResult(AccessToken);
            }

            var path = Path.Combine(_persistenceDirectory, $"{_uniqueStorageName}_accesstoken");

            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : AccessToken);
        }

        public override Task PersistGuardDataAsync(string data, CancellationToken cancellationToken = default)
        {
            GuardData = data;

            if (string.IsNullOrEmpty(_persistenceDirectory)) return Task.CompletedTask;

            Directory.CreateDirectory(_persistenceDirectory);
            File.WriteAllText(Path.Combine(_persistenceDirectory, $"{_uniqueStorageName}_guarddata"), GuardData);

            return Task.CompletedTask;
        }

        public override Task<string> GetGuardDataAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_persistenceDirectory))
            {
                return Task.FromResult(GuardData);
            }

            var path = Path.Combine(_persistenceDirectory, $"{_uniqueStorageName}_guarddata");

            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : GuardData);
        }
    }
}