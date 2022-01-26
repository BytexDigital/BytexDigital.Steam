using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Exceptions;

using Nito.AsyncEx;

using SteamKit2;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using SteamKit = SteamKit2;

namespace BytexDigital.Steam.Core
{
    public class SteamClient : IDisposable
    {
        public SteamCredentials Credentials { get; private set; }
        public SteamKit.SteamClient InternalClient { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        /// <summary>
        /// Indicates whether the client is ready for any operation.
        /// </summary>
        public bool IsConnected => _clientReadyEvent.IsSet;

        /// <summary>
        /// Indicates whether the client is faulted. See <see cref="FaultReason"/> for more information.
        /// </summary>
        public bool IsFaulted => FaultException != null;

        public string TwoFactorCode { get; set; }
        public string EmailAuthCode { get; private set; }

        public int MaximumLogonAttempts { get; set; } = 1;

        public uint SuggestedCellId { get; private set; }

        public Exception FaultException { get; private set; }

        internal SteamKit.CallbackManager CallbackManager { get; set; }
        internal IList<SteamKit.SteamApps.LicenseListCallback.License> Licenses { get; set; }

        internal readonly SteamUser _steamUserHandler;
        internal readonly SteamApps _steamAppsHandler;
        internal readonly SteamContent _steamContentHandler;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly AsyncManualResetEvent _clientReadyEvent = new AsyncManualResetEvent(false);
        private readonly AsyncManualResetEvent _clientFaultedEvent = new AsyncManualResetEvent(false);
        private readonly SteamAuthenticationCodesProvider _codesProvider;
        private readonly SteamAuthenticationFilesProvider _authenticationProvider;
        private bool _isClientRunning = false;
        private int _logonAttemptsCounter = 0;

        public SteamClient(SteamCredentials credentials) : this(credentials, new DefaultSteamAuthenticationCodesProvider(), new DefaultSteamAuthenticationFilesProvider())
        {
        }

        public SteamClient(SteamCredentials credentials, SteamAuthenticationCodesProvider codesProvider, SteamAuthenticationFilesProvider authenticationProvider)
        {
            Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _codesProvider = codesProvider;
            _authenticationProvider = authenticationProvider;
            InternalClient = new SteamKit.SteamClient();

            _cancellationTokenSource = new CancellationTokenSource();
            CallbackManager = new SteamKit.CallbackManager(InternalClient);

            _steamUserHandler = InternalClient.GetHandler<SteamUser>();
            _steamAppsHandler = InternalClient.GetHandler<SteamApps>();
            _steamContentHandler = InternalClient.GetHandler<SteamContent>();

            Task.Run(async () => await CallbackManagerHandler());

            CallbackManager.Subscribe<SteamKit.SteamClient.ConnectedCallback>(OnConnected);
            CallbackManager.Subscribe<SteamKit.SteamClient.DisconnectedCallback>(OnDisconnected);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoggedOnCallback>(OnLoggedOn);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoggedOffCallback>(OnLoggedOff);
            CallbackManager.Subscribe<SteamKit.SteamApps.LicenseListCallback>(OnLicenseList);
            CallbackManager.Subscribe<SteamKit.SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
            CallbackManager.Subscribe<SteamKit.SteamUser.LoginKeyCallback>(OnLoginKey);

            InternalClient.Connect();
        }

        public void Shutdown()
        {
            Dispose();
        }

        /// <summary>
        /// Asks the underlying client to connect to Steam and perform a login with the given <see cref="Credentials"/>.
        /// </summary>
        /// <returns>True if successfully connected.</returns>
        /// <exception cref="SteamClientFaultedException">Client is faulted.</exception>
        public async Task ConnectAsync() => await ConnectAsync(CancellationToken.None);

        /// <summary>
        /// Asks the underlying client to connect to Steam and perform a login with the given <see cref="Credentials"/>.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="SteamClientFaultedException">Client is faulted.</exception>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (_isClientRunning) return;

            InternalClient.Connect();

            var readyTask = _clientReadyEvent.WaitAsync(cancellationToken);
            var faultedTask = _clientFaultedEvent.WaitAsync();

            var task = await Task.WhenAny(readyTask, faultedTask);

            if (task == faultedTask) throw new SteamClientFaultedException(FaultException);
        }

        /// <summary>
        /// Awaits client to be ready for operational use. Throws exception in any other case.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Awaitable.</returns>
        /// <exception cref="SteamClientFaultedException"></exception>
        /// <exception cref="SteamClientNotReadyException"></exception>
        public async Task AwaitReadyAsync(CancellationToken cancellationToken)
        {
            var readyTask = _clientReadyEvent.WaitAsync(cancellationToken);
            var faultedTask = _clientFaultedEvent.WaitAsync();

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

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            InternalClient.Disconnect();
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
                        LoginID = (uint)new Random().Next(100000000, 999999999)
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

        private void OnLoginKey(SteamUser.LoginKeyCallback callback)
        {
            _authenticationProvider.SaveLoginKey(Credentials, callback.LoginKey);
            _steamUserHandler.AcceptNewLoginKey(callback);
        }

        private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            byte[] hash = default;

            using (var sha = SHA1.Create())
            {
                hash = sha.ComputeHash(callback.Data);
            }

            _authenticationProvider.SaveSentryFileContent(Credentials, callback.Data);

            Task.Run(() =>
            {
                _steamUserHandler.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
                {
                    JobID = callback.JobID,

                    FileName = callback.FileName,

                    BytesWritten = callback.BytesToWrite,
                    FileSize = callback.Data.Length,
                    Offset = callback.Offset,

                    Result = EResult.OK,
                    LastError = 0,

                    OneTimePassword = callback.OneTimePassword,

                    SentryFileHash = hash
                });
            });
        }

        private void OnLicenseList(SteamApps.LicenseListCallback callback)
        {
            Licenses = new List<SteamApps.LicenseListCallback.License>(callback.LicenseList);
        }

        private void OnConnected(SteamKit.SteamClient.ConnectedCallback callback)
        {
            AttemptLogin();
        }

        private void OnDisconnected(SteamKit.SteamClient.DisconnectedCallback callback)
        {
            _clientReadyEvent.Reset();

            if (_cancellationTokenSource.IsCancellationRequested || callback.UserInitiated) return;

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));

                InternalClient.Connect();
            });
        }

        private void OnLoggedOn(SteamKit.SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                SuggestedCellId = callback.CellID;

                _clientReadyEvent.Set();
            }
            else
            {
                _logonAttemptsCounter++;

                if (_logonAttemptsCounter > MaximumLogonAttempts)
                {
                    FaultException = new SteamLogonException(callback.Result);
                    _clientFaultedEvent.Set();
                }

                if (callback.Result == EResult.AccountLogonDenied || callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
                {
                    if (callback.Result == EResult.AccountLogonDenied)
                    {
                        EmailAuthCode = _codesProvider.GetEmailAuthenticationCode(Credentials);
                    }
                    else
                    {
                        TwoFactorCode = _codesProvider.GetTwoFactorAuthenticationCode(Credentials);
                    }

                    Task.Run(() => InternalClient.Connect());
                }
                else
                {
                    FaultException = new SteamLogonException(callback.Result);
                    _clientFaultedEvent.Set();
                }
            }
        }

        private void OnLoggedOff(SteamKit.SteamUser.LoggedOffCallback callback)
        {
            _clientReadyEvent.Reset();
        }
    }
}
