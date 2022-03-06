using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Exceptions;
using Nito.AsyncEx;
using SteamKit = SteamKit2;

namespace BytexDigital.Steam.Core
{
    public class SteamClient : IDisposable
    {
        private readonly SteamAuthenticationFilesProvider _authenticationProvider;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly AsyncManualResetEvent _clientFaultedEvent = new AsyncManualResetEvent(false);
        private readonly AsyncManualResetEvent _clientReadyEvent = new AsyncManualResetEvent(false);
        private readonly AsyncSemaphore _clientReconnectSemaphore = new AsyncSemaphore(1);
        private readonly SteamAuthenticationCodesProvider _codesProvider;
        private readonly bool _isClientRunning = false;
        internal readonly SteamKit.SteamApps _steamAppsHandler;
        internal readonly SteamKit.SteamContent _steamContentHandler;

        internal readonly SteamKit.SteamUser _steamUserHandler;
        private int _logonAttemptsCounter;

        public SteamCredentials Credentials { get; }
        public SteamKit.SteamClient InternalClient { get; }
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        /// <summary>
        ///     Indicates whether the client is ready for any operation.
        /// </summary>
        public bool IsConnected => _clientReadyEvent.IsSet;

        /// <summary>
        ///     Indicates whether the client is faulted. See <see cref="FaultReason" /> for more information.
        /// </summary>
        public bool IsFaulted => FaultException != null;

        public string TwoFactorCode { get; set; }
        public string EmailAuthCode { get; private set; }

        public int MaximumLogonAttempts { get; set; } = 1;

        public uint SuggestedCellId { get; private set; }

        public Exception FaultException { get; private set; }

        internal SteamKit.CallbackManager CallbackManager { get; set; }
        internal IList<SteamKit.SteamApps.LicenseListCallback.License> Licenses { get; set; }

        public SteamClient(SteamCredentials credentials) : this(credentials,
            new DefaultSteamAuthenticationCodesProvider(), new DefaultSteamAuthenticationFilesProvider())
        {
        }

        public SteamClient(SteamCredentials credentials, SteamAuthenticationCodesProvider codesProvider,
            SteamAuthenticationFilesProvider authenticationProvider)
        {
            Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _codesProvider = codesProvider;
            _authenticationProvider = authenticationProvider;
            InternalClient = new SteamKit.SteamClient();

            _cancellationTokenSource = new CancellationTokenSource();
            CallbackManager = new SteamKit.CallbackManager(InternalClient);

            _steamUserHandler = InternalClient.GetHandler<SteamKit.SteamUser>();
            _steamAppsHandler = InternalClient.GetHandler<SteamKit.SteamApps>();
            _steamContentHandler = InternalClient.GetHandler<SteamKit.SteamContent>();

            Task.Run(async () => await CallbackManagerHandler());

            CallbackManager.Subscribe<SteamKit.SteamClient.ConnectedCallback>(OnConnected);
            CallbackManager.Subscribe<SteamKit.SteamClient.DisconnectedCallback>(OnDisconnected);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoggedOnCallback>(OnLoggedOn);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoggedOffCallback>(OnLoggedOff);
            CallbackManager.Subscribe<SteamKit.SteamApps.LicenseListCallback>(OnLicenseList);
            CallbackManager.Subscribe<SteamKit.SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoginKeyCallback>(OnLoginKey);
        }

        public void Dispose()
        {
            FaultException ??= new SteamClientDisposedException();
            _clientFaultedEvent.Set();

            _cancellationTokenSource.Cancel();
            InternalClient.Disconnect();
        }

        public event Action InternalClientConnected;
        public event Action InternalClientDisconnected;
        public event Action InternalClientLoggedOn;
        public event Action InternalClientLoggedOff;

        public void Shutdown()
        {
            Dispose();
        }

        /// <summary>
        ///     Asks the underlying client to connect to Steam and perform a login with the given <see cref="Credentials" />.
        /// </summary>
        /// <returns>True if successfully connected.</returns>
        /// <exception cref="SteamClientFaultedException">Client is faulted.</exception>
        public async Task ConnectAsync()
        {
            await ConnectAsync(CancellationToken.None);
        }

        /// <summary>
        ///     Asks the underlying client to connect to Steam and perform a login with the given <see cref="Credentials" />.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="SteamClientFaultedException">Client is faulted.</exception>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_isClientRunning)
            {
                return;
            }

            if (_cancellationTokenSource.IsCancellationRequested)
            {
                throw new SteamClientDisposedException();
            }

            InternalClient.Connect();

            var readyTask = _clientReadyEvent.WaitAsync(cancellationToken);
            var faultedTask = _clientFaultedEvent.WaitAsync(cancellationToken);

            var task = await Task.WhenAny(readyTask, faultedTask);

            if (task == faultedTask)
            {
                throw new SteamClientFaultedException(FaultException);
            }
        }

        /// <summary>
        ///     Awaits client to be ready for operational use. Throws exception in any other case.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Awaitable.</returns>
        /// <exception cref="SteamClientFaultedException"></exception>
        /// <exception cref="SteamClientNotReadyException"></exception>
        public async Task AwaitReadyAsync(CancellationToken cancellationToken)
        {
            var readyTask = _clientReadyEvent.WaitAsync(cancellationToken);
            var faultedTask = _clientFaultedEvent.WaitAsync(cancellationToken);

            var task = await Task.WhenAny(readyTask, faultedTask);

            if (task == faultedTask)
            {
                throw new SteamClientFaultedException(FaultException);
            }

            if (!readyTask.IsCompletedSuccessfully)
            {
                throw new SteamClientNotReadyException();
            }
        }

        public SteamOs GetSteamOs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return SteamOs.Windows;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return SteamOs.Linux;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return SteamOs.MacOS;
            }

            return new SteamOs("unknown");
        }

        private void AttemptLogin()
        {
            if (!Credentials.IsAnonymous)
            {
                byte[] hash = null;

                using (var sha = SHA1.Create())
                {
                    var data = _authenticationProvider.GetSentryFileContent(Credentials);

                    if (data != null)
                    {
                        hash = sha.ComputeHash(data);
                    }
                }

                Task.Run(() =>
                {
                    _steamUserHandler.LogOn(new SteamKit.SteamUser.LogOnDetails
                    {
                        Username = Credentials.Username,
                        Password = Credentials.Password,
                        TwoFactorCode = TwoFactorCode,
                        AuthCode = EmailAuthCode,
                        SentryFileHash = hash,
                        LoginKey = _authenticationProvider.GetLoginKey(Credentials),
                        ShouldRememberPassword = true,
                        LoginID = (uint) new Random().Next(100000000, 999999999)
                    });
                });
            }
            else
            {
                _steamUserHandler.LogOnAnonymous();
            }
        }

        private async Task CallbackManagerHandler()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                CallbackManager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }

        private void OnLoginKey(SteamKit.SteamUser.LoginKeyCallback callback)
        {
            _authenticationProvider.SaveLoginKey(Credentials, callback.LoginKey);
            _steamUserHandler.AcceptNewLoginKey(callback);
        }

        private void OnMachineAuth(SteamKit.SteamUser.UpdateMachineAuthCallback callback)
        {
            byte[] hash = default;

            using (var sha = SHA1.Create())
            {
                hash = sha.ComputeHash(callback.Data);
            }

            _authenticationProvider.SaveSentryFileContent(Credentials, callback.Data);

            Task.Run(() =>
            {
                _steamUserHandler.SendMachineAuthResponse(new SteamKit.SteamUser.MachineAuthDetails
                {
                    JobID = callback.JobID,

                    FileName = callback.FileName,

                    BytesWritten = callback.BytesToWrite,
                    FileSize = callback.Data.Length,
                    Offset = callback.Offset,

                    Result = SteamKit.EResult.OK,
                    LastError = 0,

                    OneTimePassword = callback.OneTimePassword,

                    SentryFileHash = hash
                });
            });
        }

        private void OnLicenseList(SteamKit.SteamApps.LicenseListCallback callback)
        {
            Licenses = new List<SteamKit.SteamApps.LicenseListCallback.License>(callback.LicenseList);
        }

        private void OnConnected(SteamKit.SteamClient.ConnectedCallback callback)
        {
            InternalClientConnected?.Invoke();

            AttemptLogin();
        }

        private void OnDisconnected(SteamKit.SteamClient.DisconnectedCallback callback)
        {
            InternalClientDisconnected?.Invoke();

            _clientReadyEvent.Reset();

            if (callback.UserInitiated)
            {
                return;
            }

            if (_cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                CancellationToken.ThrowIfCancellationRequested();

                // Keep this thread hostage until noone is currently performing work that might change how we sign into Steam, e.g.
                // authentication details such as 2FA code
                using var lk = await _clientReconnectSemaphore.LockAsync(CancellationToken);

                CancellationToken.ThrowIfCancellationRequested();

                InternalClient.Connect();
            });
        }

        private async void OnLoggedOn(SteamKit.SteamUser.LoggedOnCallback callback)
        {
            InternalClientLoggedOn?.Invoke();

            // Locking this semaphore makes the reconnect logic halt until we are done in this method
            using var lk = await _clientReconnectSemaphore.LockAsync();

            if (callback.Result == SteamKit.EResult.OK)
            {
                SuggestedCellId = callback.CellID;

                _logonAttemptsCounter = 0;

                _clientReadyEvent.Set();
            }
            else
            {
                _logonAttemptsCounter++;

                // Don't spam unsuccessful logon attempts as this might get us rate limited
                if (_logonAttemptsCounter > MaximumLogonAttempts)
                {
                    FaultException = new SteamLogonException(callback.Result);
                    _clientFaultedEvent.Set();
                    _cancellationTokenSource.Cancel();

                    return;
                }

                if (callback.Result == SteamKit.EResult.AccountLogonDenied ||
                    callback.Result == SteamKit.EResult.AccountLoginDeniedNeedTwoFactor)
                {
                    if (callback.Result == SteamKit.EResult.AccountLogonDenied)
                    {
                        EmailAuthCode = _codesProvider.GetEmailAuthenticationCode(Credentials);
                    }
                    else
                    {
                        TwoFactorCode = _codesProvider.GetTwoFactorAuthenticationCode(Credentials);
                    }

                    // By returning, we are releasing the aquired semaphore. This will make the disconnected
                    // callback continue it's execution and attempt a reconnect if our client hasn't been marked
                    // as cancelled/closed.
                    return;
                }

                FaultException = new SteamLogonException(callback.Result);
                _clientFaultedEvent.Set();
                _cancellationTokenSource.Cancel();
            }
        }

        private void OnLoggedOff(SteamKit.SteamUser.LoggedOffCallback callback)
        {
            InternalClientLoggedOff?.Invoke();

            _clientReadyEvent.Reset();
        }
    }
}