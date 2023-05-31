using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Exceptions;
using BytexDigital.Steam.Core.Regional;
using Nito.AsyncEx;
using SteamKit2.Authentication;
using SteamKit2.Discovery;
using SteamKit = SteamKit2;

namespace BytexDigital.Steam.Core
{
    public class SteamClient : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly AsyncManualResetEvent _clientFaultedEvent = new AsyncManualResetEvent(false);
        private readonly AsyncManualResetEvent _clientReadyEvent = new AsyncManualResetEvent(false);
        private readonly AsyncSemaphore _clientReconnectSemaphore = new AsyncSemaphore(1);
        private readonly bool _isClientRunning = false;
        internal readonly SteamKit.SteamApps _steamAppsHandler;
        private readonly SteamAuthenticator _steamAuthenticator;
        internal readonly SteamKit.SteamContent _steamContentHandler;
        internal readonly SteamKit.SteamUser _steamUserHandler;
        internal AsyncManualResetEvent _licensesReceived = new AsyncManualResetEvent(false);
        private int _logonAttemptsCounter;

        /// <summary>
        ///     Credentials that this instance will use to log in after connecting.
        /// </summary>
        public SteamCredentials Credentials { get; }

        /// <summary>
        ///     Internal client provided by the SteamKit library.
        /// </summary>
        public SteamKit.SteamClient InternalClient { get; }

        /// <summary>
        ///     Server that the client will be forced to connect to.
        ///     <para>If unavailable, the client will attempt reconnecting until a shutdown is forced.</para>
        /// </summary>
        public ServerRecord ForcedServer { get; set; }

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        /// <summary>
        ///     Indicates whether the client is ready for any operation.
        /// </summary>
        public bool IsConnected => _clientReadyEvent.IsSet;

        /// <summary>
        ///     Indicates whether the client is faulted. See <see cref="FaultReason" /> for more information.
        /// </summary>
        public bool IsFaulted => FaultException != null;

        public int MaximumLogonAttempts { get; set; } = 1;
        public uint ActiveCellId { get; private set; }
        public Exception FaultException { get; private set; }

        internal SteamKit.CallbackManager CallbackManager { get; set; }
        internal IList<SteamKit.SteamApps.LicenseListCallback.License> Licenses { get; set; }

        public SteamClient(SteamCredentials credentials = default) : this(
            credentials,
            default,
            default)
        {
        }

        public SteamClient(
            SteamCredentials credentials = default,
            SteamAuthenticator steamAuthenticator = default) : this(
            credentials,
            steamAuthenticator,
            default)
        {
        }

        public SteamClient(
            SteamCredentials credentials = default,
            SteamAuthenticator steamAuthenticator = default,
            Action<SteamKit.ISteamConfigurationBuilder> steamConfigurationBuilder = default,
            IRegionSpecificServerListProvider serverListProvider = default)
        {
            // Default values
            credentials ??= SteamCredentials.Anonymous;
            steamConfigurationBuilder ??= builder => { };

            Credentials = credentials;
            _steamAuthenticator = steamAuthenticator ?? throw new SteamAuthenticatorRequiredException();

            // If the caller has provided a region specific server list,
            // make sure we also use it in our SteamConfiguration object.
            if (serverListProvider != null)
            {
                var userPassedBuilder = steamConfigurationBuilder;

                steamConfigurationBuilder = builder =>
                {
                    builder.WithServerListProvider(serverListProvider);

                    // Invoke the user passed config builder afterwards
                    userPassedBuilder.Invoke(builder);
                };
            }

            // SteamKit2 SteamConfiguration object
            var configuration = SteamKit.SteamConfiguration.Create(steamConfigurationBuilder);

            // Region specific server list providers are provided the SteamConfiguration object to make API calls,
            // for example to ISteamDirectory.
            serverListProvider?.SetSteamConfiguration(configuration);

            // Initialize SteamClient with the configuration object
            InternalClient = new SteamKit.SteamClient(configuration);

            _cancellationTokenSource = new CancellationTokenSource();
            CallbackManager = new SteamKit.CallbackManager(InternalClient);

            _steamUserHandler = InternalClient.GetHandler<SteamKit.SteamUser>();
            _steamAppsHandler = InternalClient.GetHandler<SteamKit.SteamApps>();
            _steamContentHandler = InternalClient.GetHandler<SteamKit.SteamContent>();

            CallbackManager.Subscribe<SteamKit.SteamClient.ConnectedCallback>(OnConnected);
            CallbackManager.Subscribe<SteamKit.SteamClient.DisconnectedCallback>(OnDisconnected);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoggedOnCallback>(OnLoggedOn);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoggedOffCallback>(OnLoggedOff);
            CallbackManager.Subscribe<SteamKit.SteamApps.LicenseListCallback>(OnLicenseList);

            Task.Run(async () => await CallbackManagerHandler());
        }

        public void Dispose()
        {
            FaultException ??= new SteamClientDisposedException();
            _clientFaultedEvent.Set();

            _cancellationTokenSource.Cancel();
            InternalClient.Disconnect();
        }

        /// <summary>
        ///     Event handler that fires upon exception that are produced by user code.
        /// </summary>
        public event EventHandler<Exception> OnUnhandledException;

        public event Action InternalClientAttemptingConnect;
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
            if (_isClientRunning) return;

            if (_cancellationTokenSource.IsCancellationRequested) throw new SteamClientDisposedException();

            InternalClientAttemptingConnect?.Invoke();

            InternalClient.Connect(ForcedServer);

            var readyTask = _clientReadyEvent.WaitAsync(cancellationToken);
            var faultedTask = _clientFaultedEvent.WaitAsync(cancellationToken);

            var task = await Task.WhenAny(readyTask, faultedTask);

            if (task == faultedTask) throw new SteamClientFaultedException(FaultException);

            // If we are an anonymous user, the callback about owned licenses will not fire.
            if (!Credentials.IsAnonymous)
            {
                // Wait for all licenses to be received before continuing.
                await _licensesReceived.WaitAsync(cancellationToken);
            }
            else
            {
                // Manually set them to nothing for an anonymous user.
                Licenses = new List<SteamKit.SteamApps.LicenseListCallback.License>();
                _licensesReceived.Set();
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

            if (task == faultedTask) throw new SteamClientFaultedException(FaultException);

            if (!readyTask.IsCompletedSuccessfully) throw new SteamClientNotReadyException();
        }

        private void AttemptLogin()
        {
            if (!Credentials.IsAnonymous)
            {
                _ = Task.Run(
                    async () =>
                    {
                        _logonAttemptsCounter++;
                        var accessToken = string.Empty;

                        try
                        {
                            accessToken =
                                await _steamAuthenticator.GetAccessTokenAsync(_cancellationTokenSource.Token);

                            if (string.IsNullOrEmpty(accessToken))
                            {
                                var guardData = await _steamAuthenticator.GetGuardDataAsync(
                                    _cancellationTokenSource
                                        .Token);

                                // Begin authenticating via credentials
                                var authSession = await InternalClient.Authentication
                                    .BeginAuthSessionViaCredentialsAsync(
                                        new AuthSessionDetails
                                        {
                                            Username = Credentials.Username,
                                            Password = Credentials.Password,
                                            IsPersistentSession = false,
                                            Authenticator = new SteamAuthenticatorAdapter(_steamAuthenticator),
                                            GuardData = string.IsNullOrEmpty(guardData) ? null : guardData
                                        })
                                    .WaitAsync(_cancellationTokenSource.Token);

                                var pollResponse = await authSession.PollingWaitForResultAsync(CancellationToken);

                                accessToken = pollResponse.RefreshToken;

                                await _steamAuthenticator.PersistAccessTokenAsync(accessToken,
                                    _cancellationTokenSource.Token);

                                if (!string.IsNullOrEmpty(pollResponse.NewGuardData))
                                {
                                    await _steamAuthenticator.PersistGuardDataAsync(pollResponse.NewGuardData,
                                        _cancellationTokenSource.Token);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            OnUnhandledException?.Invoke(this, ex);
                        }

                        _steamUserHandler.LogOn(
                            new SteamKit.SteamUser.LogOnDetails
                            {
                                Username = Credentials.Username,
                                AccessToken = accessToken,
                                LoginID = (uint) new Random().Next(100000000, 999999999)
                            });
                    });
            }
            else
            {
                _steamUserHandler.LogOnAnonymous();
            }
        }

        private Task CallbackManagerHandler()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                CallbackManager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(10));
            }

            return Task.CompletedTask;
        }

        // private void OnLoginKey(SteamKit.SteamUser.LoginKeyCallback callback)
        // {
        //     _authenticationProvider.SaveLoginKey(Credentials, callback.LoginKey);
        //     _steamUserHandler.AcceptNewLoginKey(callback);
        // }

        // private void OnMachineAuth(SteamKit.SteamUser.UpdateMachineAuthCallback callback)
        // {
        //     byte[] hash = default;
        //
        //     using (var sha = SHA1.Create())
        //     {
        //         hash = sha.ComputeHash(callback.Data);
        //     }
        //
        //     _authenticationProvider.SaveSentryFileContent(Credentials, callback.Data);
        //
        //     Task.Run(
        //         () =>
        //         {
        //             _steamUserHandler.SendMachineAuthResponse(
        //                 new SteamKit.SteamUser.MachineAuthDetails
        //                 {
        //                     JobID = callback.JobID,
        //
        //                     FileName = callback.FileName,
        //
        //                     BytesWritten = callback.BytesToWrite,
        //                     FileSize = callback.Data.Length,
        //                     Offset = callback.Offset,
        //
        //                     Result = SteamKit.EResult.OK,
        //                     LastError = 0,
        //
        //                     OneTimePassword = callback.OneTimePassword,
        //
        //                     SentryFileHash = hash
        //                 });
        //         });
        // }

        private void OnLicenseList(SteamKit.SteamApps.LicenseListCallback callback)
        {
            Licenses = new List<SteamKit.SteamApps.LicenseListCallback.License>(callback.LicenseList);

            _licensesReceived.Set();
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

            if (callback.UserInitiated) return;

            if (CancellationToken.IsCancellationRequested) return;

            _ = Task.Run(
                async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken);

                    CancellationToken.ThrowIfCancellationRequested();

                    // Keep this thread hostage until noone is currently performing work that might change how we sign into Steam, e.g.
                    // authentication details such as 2FA code
                    using var lk = await _clientReconnectSemaphore.LockAsync(CancellationToken);

                    CancellationToken.ThrowIfCancellationRequested();

                    InternalClientAttemptingConnect?.Invoke();

                    InternalClient.Connect(ForcedServer);
                });
        }

        private async void OnLoggedOn(SteamKit.SteamUser.LoggedOnCallback callback)
        {
            InternalClientLoggedOn?.Invoke();

            // Locking this semaphore makes the reconnect logic halt until we are done in this method
            using var lk = await _clientReconnectSemaphore.LockAsync();

            if (callback.Result == SteamKit.EResult.OK)
            {
                // Set the cell ID our Steam Client actually connected to
                ActiveCellId = callback.CellID;

                _logonAttemptsCounter = 0;

                _clientReadyEvent.Set();
            }
            else
            {
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
                    // if (callback.Result == SteamKit.EResult.AccountLogonDenied)
                    //     _emailCode = _steamAuthenticator.GetEmailAuthenticationCode(Credentials);
                    // else
                    //     _twoFactorCode = _steamAuthenticator.GetTwoFactorAuthenticationCode(Credentials);

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