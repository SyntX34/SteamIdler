using SteamKit2;
using SteamKit2.Authentication;
using Newtonsoft.Json;

namespace SteamGameIdler.Shared;

public enum LoginResult
{
    Success,
    Needs2FA,
    NeedsEmailCode,
    NeedsDeviceCode,
    InvalidPassword,
    AccountLocked,
    NetworkError,
    Unknown,
    SessionExpired
}
internal sealed class ConsoleAuthenticator : IAuthenticator
{
    public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
            Console.WriteLine("(The previous code was incorrect, please try again)");
        Console.Write("Enter your Steam Guard mobile authenticator code: ");
        return await Task.FromResult(Console.ReadLine()?.Trim() ?? "");
    }

    public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
            Console.WriteLine("(The previous code was incorrect, please try again)");
        Console.Write($"Enter the Steam Guard code sent to {email}: ");
        return await Task.FromResult(Console.ReadLine()?.Trim() ?? "");
    }

    public async Task<bool> AcceptDeviceConfirmationAsync()
    {
        Console.WriteLine("Check your Steam mobile app and confirm the login.");
        Console.WriteLine("(Press Enter here once you've confirmed, or wait...)");
        await Task.Run(() => Console.ReadLine());
        return true;
    }
}

public class SteamAuth
{
    private SteamClient? _steamClient;
    private CallbackManager? _manager;
    private SteamUser? _steamUser;
    private SteamApps? _steamApps;
    private SteamFriends? _steamFriends;
    private CancellationTokenSource? _pumpCts;

    private readonly AppConfig _config;
    private readonly IAuthenticator _authenticator;

    private TaskCompletionSource<bool>? _connectTcs;
    private TaskCompletionSource<LoginResult>? _loginTcs;

    private volatile bool _isRunning;
    private volatile bool _intentionalDisconnect;
    private volatile bool _isLoggedIn;
    private CancellationToken _appToken;
    private volatile bool _isReconnecting;
    private volatile bool _sessionPermanentlyExpired;
    private int _reconnectAttemptCount;
    private const int MaxReconnectAttempts = 5;
    private DateTime _lastDisconnectLog = DateTime.MinValue;
    private static readonly TimeSpan DisconnectLogThrottle = TimeSpan.FromSeconds(5);

    private readonly HashSet<ulong> _repliedTo = new();
    private static readonly string ChatLogPath = "chat_log.txt";

    public event Action<string, string>? OnChatMessage;
    public event Action<string>? OnSystemEvent;
    public event Action? OnLoggedInElsewhere;
    public event Action? OnReconnected;
    public event Action<string>? OnFatalError;

    public SteamID? LoggedInSteamID { get; private set; }
    public bool IsConnected => _steamClient?.IsConnected ?? false;
    public bool IsLoggedIn  => _isLoggedIn;
    public bool IsSessionExpired => _sessionPermanentlyExpired;

    public SteamUser?    SteamUser    => _steamUser;
    public SteamApps?    SteamApps    => _steamApps;
    public SteamFriends? SteamFriends => _steamFriends;

    /// <summary>
    /// Pass a custom <see cref="IAuthenticator"/> to handle Steam Guard prompts
    /// (e.g. a GUI dialog). Defaults to <see cref="ConsoleAuthenticator"/>.
    /// </summary>
    public SteamAuth(AppConfig config, IAuthenticator? authenticator = null)
    {
        _config        = config;
        _authenticator = authenticator ?? new ConsoleAuthenticator();
        InitClient();
    }

    private void InitClient()
    {
        // Dispose old client & pump before creating new ones
        CleanupClient();

        _steamClient = new SteamClient();
        _manager     = new CallbackManager(_steamClient);
        _steamUser   = _steamClient.GetHandler<SteamUser>()!;
        _steamApps   = _steamClient.GetHandler<SteamApps>()!;
        _steamFriends= _steamClient.GetHandler<SteamFriends>()!;
        RegisterCallbacks();
    }

    private void CleanupClient()
    {
        try
        {
            if (_steamClient != null && _steamClient.IsConnected)
            {
                _steamClient.Disconnect();
            }
        }
        catch { /* best-effort cleanup */ }

        // Stop the old pump
        if (_pumpCts != null)
        {
            try { _pumpCts.Cancel(); } catch { }
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        _steamClient = null;
        _manager = null;
        _steamUser = null;
        _steamApps = null;
        _steamFriends = null;
    }

    private void RegisterCallbacks()
    {
        if (_manager == null) return;
        _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _manager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMessage);
    }

    /*
     * Public API
     */

    public void Send(IClientMsg msg) => _steamClient?.Send(msg);

    public void StartCallbackPump(CancellationToken token)
    {
        _appToken = token;
        _isRunning = true;

        // Kill any previous pump before starting a new one
        if (_pumpCts != null)
        {
            try { _pumpCts.Cancel(); } catch { }
            _pumpCts.Dispose();
        }

        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task.Run(() =>
        {
            while (_isRunning && !_pumpCts.IsCancellationRequested)
            {
                try
                {
                    _manager?.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Swallow pump-level exceptions to keep the loop alive
                }
            }
        }, _pumpCts.Token);
    }

    public async Task<bool> ConnectAsync(CancellationToken token = default)
    {
        if (_steamClient == null) return false;
        _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _intentionalDisconnect = false;
        _steamClient.Connect();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await _connectTcs.Task.WaitAsync(cts.Token); }
        catch { return false; }
    }

    public async Task<bool> LoginAsync(CancellationToken token = default)
    {
        if (_steamClient == null) return false;

        // If we already know the session is permanently expired, skip token login
        if (!_sessionPermanentlyExpired && !string.IsNullOrWhiteSpace(_config.RefreshToken))
        {
            Emit("Found saved session token — logging in automatically...");
            try
            {
                if (await DoTokenLoginAsync(_config.RefreshToken, token))
                {
                    Emit("✓ Logged in with saved token (no password needed)");
                    return true;
                }
                Emit("Saved token was rejected — falling back to password login.");
            }
            catch (Exception ex)
            {
                Emit($"Token login failed ({ex.Message}) — falling back to password login.");
            }
        }

        // If token login failed and we have credentials, try password login
        if (!string.IsNullOrWhiteSpace(_config.Username) && !string.IsNullOrWhiteSpace(_config.Password))
        {
            return await DoPasswordLoginAsync(token);
        }

        Emit("✗ No credentials available to log in.");
        return false;
    }

    public bool SendMessage(ulong steamId64, string message)
    {
        if (!_isLoggedIn || _steamFriends == null) return false;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var target = new SteamID(steamId64);
        _steamFriends.SendChatMessage(target, EChatEntryType.ChatMsg, message);

        LogChat($"[You → {steamId64}] {message}");
        Emit($"[Sent to {steamId64}]: {message}");
        return true;
    }

    public List<(ulong Id, string Name, string State)> GetFriendList()
    {
        var result = new List<(ulong, string, string)>();
        if (_steamFriends == null) return result;

        int count = _steamFriends.GetFriendCount();
        for (int i = 0; i < count; i++)
        {
            var sid   = _steamFriends.GetFriendByIndex(i);
            var name  = _steamFriends.GetFriendPersonaName(sid) ?? sid.ToString();
            var state = _steamFriends.GetFriendPersonaState(sid).ToString();
            result.Add((sid.ConvertToUInt64(), name, state));
        }
        return result;
    }

    public void Stop()
    {
        _intentionalDisconnect = true;
        _isRunning  = false;
        _isLoggedIn = false;
        _sessionPermanentlyExpired = false;
        _isReconnecting = false;
        _reconnectAttemptCount = 0;

        try
        {
            _steamUser?.LogOff();
        }
        catch { }

        CleanupClient();
    }

    /*
     * Login Helpers
     */

    private async Task<bool> DoTokenLoginAsync(string refreshToken, CancellationToken token)
    {
        _loginTcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _steamUser?.LogOn(new SteamUser.LogOnDetails
        {
            Username    = _config.Username,
            AccessToken = refreshToken,
            LoginID     = (uint)Random.Shared.Next(1, 999999)
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var r = await _loginTcs.Task.WaitAsync(cts.Token);
            if (r == LoginResult.Success)
                return true;

            // If the token was explicitly rejected, mark as expired
            if (r == LoginResult.InvalidPassword || r == LoginResult.SessionExpired || r == LoginResult.Unknown)
            {
                _sessionPermanentlyExpired = true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> DoPasswordLoginAsync(CancellationToken token)
    {
        if (_steamClient == null) return false;
        Emit("Starting credential login...");

        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username           = _config.Username,
                    Password           = _config.Password,
                    IsPersistentSession= true,
                    Authenticator      = _authenticator
                });

            var pollResult = await authSession.PollingWaitForResultAsync(token);

            _config.RefreshToken = pollResult.RefreshToken;
            _config.Save();
            Emit("✓ Session token saved — won't need an auth code again for a long time.");

            // Reset session expired flag since we just got a fresh token
            _sessionPermanentlyExpired = false;

            _loginTcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username    = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
                LoginID     = (uint)Random.Shared.Next(1, 999999)
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var logonResult = await _loginTcs.Task.WaitAsync(cts.Token);
            return logonResult == LoginResult.Success;
        }
        catch (TaskCanceledException) { return false; }
        catch (Exception ex)
        {
            Emit($"Login error: {ex.Message}");
            return false;
        }
    }

    /*
     * Reconnect - completely rewritten to prevent memory/CPU leaks
     */

    private void BeginAutoReconnect()
    {
        // Prevent multiple concurrent reconnect loops
        if (_isReconnecting)
        {
            EmitThrottled("Reconnect already in progress — skipping duplicate.");
            return;
        }

        // If session is permanently expired, don't keep retrying
        if (_sessionPermanentlyExpired)
        {
            Emit("✗ Session token is permanently expired. Automatic reconnection is not possible.");
            Emit("  Please restart the application and re-enter your credentials.");
            OnFatalError?.Invoke("Session expired permanently. Please restart and re-authenticate.");
            return;
        }

        _isReconnecting = true;

        Task.Run(async () =>
        {
            try
            {
                while (_isRunning && !_appToken.IsCancellationRequested && !_sessionPermanentlyExpired)
                {
                    if (_reconnectAttemptCount >= MaxReconnectAttempts)
                    {
                        Emit($"✗ Maximum reconnection attempts ({MaxReconnectAttempts}) reached. Giving up.");
                        Emit("  Please restart the application to try again.");
                        OnFatalError?.Invoke($"Could not reconnect after {MaxReconnectAttempts} attempts. Please restart.");
                        return;
                    }

                    _reconnectAttemptCount++;

                    // Exponential backoff: 5s, 10s, 20s, 40s, 80s (capped at 60s)
                    int delaySec = Math.Min(60, (int)Math.Pow(2, _reconnectAttemptCount) * 5 / 2);
                    Emit($"Reconnect attempt {_reconnectAttemptCount}/{MaxReconnectAttempts} in {delaySec}s...");
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), _appToken).ContinueWith(_ => { });

                    if (!_isRunning || _appToken.IsCancellationRequested || _sessionPermanentlyExpired) break;

                    // Cleanup old client and create fresh one (critical to prevent memory leak)
                    InitClient();
                    StartCallbackPump(_appToken);

                    if (!await ConnectAsync(_appToken))
                    {
                        Emit("  Connection failed — will retry.");
                        continue;
                    }

                    if (!await LoginAsync(_appToken))
                    {
                        Emit("  Login failed — will retry.");

                        // If the session died permanently during login, stop retrying
                        if (_sessionPermanentlyExpired)
                        {
                            Emit("  Token expired permanently. Stop reconnecting.");
                            OnFatalError?.Invoke("Session expired permanently. Please restart and re-authenticate.");
                            return;
                        }
                        continue;
                    }

                    // Success!
                    Emit("✓ Reconnected successfully!");
                    _reconnectAttemptCount = 0;
                    _isReconnecting = false;
                    OnReconnected?.Invoke();
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Emit($"Reconnect error: {ex.Message}");
            }
            finally
            {
                _isReconnecting = false;
            }
        });
    }

    /*
     * Callbacks
     */

    private void OnConnected(SteamClient.ConnectedCallback _)
    {
        EmitThrottled("✓ Connected to Steam network");
        _connectTcs?.TrySetResult(true);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback _)
    {
        _isLoggedIn = false;
        EmitThrottled("Disconnected from Steam.");

        if (_intentionalDisconnect)
        {
            _isRunning = false;
            return;
        }

        _connectTcs?.TrySetResult(false);
        BeginAutoReconnect();
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result == EResult.OK)
        {
            _isLoggedIn     = true;
            LoggedInSteamID = cb.ClientSteamID;
            _sessionPermanentlyExpired = false; // we're logged in, so session is fine
            Emit($"✓ Logged on! SteamID: {cb.ClientSteamID}");
            _steamFriends?.SetPersonaState(EPersonaState.Online);
            _loginTcs?.TrySetResult(LoginResult.Success);
        }
        else
        {
            Emit($"Login failed: {cb.Result}");

            if (cb.Result == EResult.LoggedInElsewhere)
            {
                Emit("⚠ Account logged in on another device!");
                OnLoggedInElsewhere?.Invoke();
            }

            // Detect expired/revoked session tokens
            if (cb.Result == EResult.InvalidPassword ||
                cb.Result == EResult.InvalidLoginAuthCode ||
                cb.Result == EResult.AccountLogonDenied ||
                cb.Result == EResult.AccountLoginDeniedNeedTwoFactor ||
                cb.Result == EResult.TwoFactorCodeMismatch ||
                cb.Result == EResult.TwoFactorActivationCodeMismatch ||
                cb.Result == EResult.AccessDenied)
            {
                _sessionPermanentlyExpired = true;
            }

            var result = cb.Result switch
            {
                EResult.InvalidPassword                    => LoginResult.InvalidPassword,
                EResult.AccountLockedDown                  => LoginResult.AccountLocked,
                EResult.AccountLogonDenied                 => LoginResult.NeedsEmailCode,
                EResult.AccountLoginDeniedNeedTwoFactor    => LoginResult.Needs2FA,
                EResult.TwoFactorCodeMismatch              => LoginResult.Needs2FA,
                EResult.TwoFactorActivationCodeMismatch    => LoginResult.Needs2FA,
                EResult.InvalidLoginAuthCode               => LoginResult.NeedsEmailCode,
                EResult.AccessDenied                       => LoginResult.SessionExpired,
                _                                          => LoginResult.Unknown
            };
            _loginTcs?.TrySetResult(result);
        }
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        _isLoggedIn = false;
        Emit($"Logged off: {cb.Result}");

        if (cb.Result == EResult.LoggedInElsewhere)
        {
            Emit("⚠ You were logged in on another device. Attempting to reclaim session...");
            OnLoggedInElsewhere?.Invoke();
        }

        // If logged off due to expired/revoked token, mark session as dead
        if (cb.Result == EResult.InvalidPassword ||
            cb.Result == EResult.AccessDenied ||
            cb.Result == EResult.InvalidLoginAuthCode)
        {
            _sessionPermanentlyExpired = true;
        }
    }

    private void OnFriendMessage(SteamFriends.FriendMsgCallback cb)
    {
        if (cb.EntryType != EChatEntryType.ChatMsg) return;
        if (string.IsNullOrWhiteSpace(cb.Message)) return;

        var name     = _steamFriends?.GetFriendPersonaName(cb.Sender) ?? cb.Sender.ToString();
        var friendId = cb.Sender.ConvertToUInt64();
        var line     = $"[{DateTime.Now:HH:mm:ss}] {name} ({friendId}): {cb.Message}";

        Emit($"[Chat] {line}");
        LogChat(line);
        OnChatMessage?.Invoke(name, cb.Message);

        if (!_config.AutoReplyEnabled) return;
        if (_repliedTo.Contains(friendId)) return;

        _repliedTo.Add(friendId);
        var reply = string.IsNullOrWhiteSpace(_config.AutoReplyMessage)
            ? "I'm currently idling games and can't chat right now."
            : _config.AutoReplyMessage;

        _steamFriends?.SendChatMessage(cb.Sender, EChatEntryType.ChatMsg, reply);
        var sentLine = $"[{DateTime.Now:HH:mm:ss}] [Auto-reply → {name}]: {reply}";
        Emit(sentLine);
        LogChat(sentLine);
    }

    /*
     * Helpers
     */

    private static void LogChat(string line)
    {
        try { File.AppendAllText(ChatLogPath, line + Environment.NewLine); }
        catch { }
    }

    /// <summary>Throttle repeated messages to avoid spamming the log (and eating memory).</summary>
    private void EmitThrottled(string msg)
    {
        var now = DateTime.Now;
        if ((now - _lastDisconnectLog) < DisconnectLogThrottle)
            return;
        _lastDisconnectLog = now;
        Emit(msg);
    }

    private void Emit(string msg)
    {
        Console.WriteLine(msg);
        OnSystemEvent?.Invoke(msg);
    }
}