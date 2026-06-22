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
    Unknown
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

    private readonly AppConfig _config;

    private TaskCompletionSource<bool>? _connectTcs;
    private TaskCompletionSource<LoginResult>? _loginTcs;

    // Reconnect state
    private volatile bool _isRunning;
    private volatile bool _intentionalDisconnect;
    private volatile bool _isLoggedIn;
    private CancellationToken _pumpToken;

    // Friends who already got an auto-reply this session
    private readonly HashSet<ulong> _repliedTo = new();

    // Chat log file
    private static readonly string ChatLogPath = "chat_log.txt";

    // Events that other components (GUI, GameIdler) can subscribe to
    public event Action<string, string>? OnChatMessage;       // (senderName, message)
    public event Action<string>? OnSystemEvent;       // general status messages
    public event Action? OnLoggedInElsewhere; // kicked by another device
    public event Action? OnReconnected;       // fired after successful re-login

    public SteamID? LoggedInSteamID { get; private set; }
    public bool IsConnected => _steamClient?.IsConnected ?? false;
    public bool IsLoggedIn => _isLoggedIn;

    public SteamUser? SteamUser => _steamUser;
    public SteamApps? SteamApps => _steamApps;
    public SteamFriends? SteamFriends => _steamFriends;

    // CTOR: Consider splitting this class into smaller pieces (e.g. separate SteamClient management from login logic) for better maintainability.
    public SteamAuth(AppConfig config)
    {
        _config = config;
        InitClient();
    }

    private void InitClient()
    {
        _steamClient = new SteamClient();
        _manager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;
        _steamFriends = _steamClient.GetHandler<SteamFriends>()!;
        RegisterCallbacks();
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
        _pumpToken = token;
        _isRunning = true;
        Task.Run(() =>
        {
            while (_isRunning && !token.IsCancellationRequested)
                _manager?.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        }, token);
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

        // Try saved refresh token first.
        if (!string.IsNullOrWhiteSpace(_config.RefreshToken))
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
            _config.RefreshToken = "";
            _config.Save();
        }

        return await DoPasswordLoginAsync(token);
    }

    /// <summary>
    /// Send a chat message to a friend by their SteamID64.
    /// Returns false if not connected/logged-in.
    /// </summary>
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

    /// <summary>
    /// Returns a list of friends: (SteamID64, Name, PersonaState).
    /// </summary>
    public List<(ulong Id, string Name, string State)> GetFriendList()
    {
        var result = new List<(ulong, string, string)>();
        if (_steamFriends == null) return result;

        int count = _steamFriends.GetFriendCount();
        for (int i = 0; i < count; i++)
        {
            var sid = _steamFriends.GetFriendByIndex(i);
            var name = _steamFriends.GetFriendPersonaName(sid) ?? sid.ToString();
            var state = _steamFriends.GetFriendPersonaState(sid).ToString();
            result.Add((sid.ConvertToUInt64(), name, state));
        }
        return result;
    }

    public void Stop()
    {
        _intentionalDisconnect = true;
        _isRunning = false;
        _isLoggedIn = false;
        _steamUser?.LogOff();
        _steamClient?.Disconnect();
    }

    /*
     * Login Helpers
     */

    private async Task<bool> DoTokenLoginAsync(string refreshToken, CancellationToken token)
    {
        _loginTcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _steamUser?.LogOn(new SteamUser.LogOnDetails
        {
            Username = _config.Username,
            AccessToken = refreshToken,
            LoginID = (uint)Random.Shared.Next(1, 999999)
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var r = await _loginTcs.Task.WaitAsync(cts.Token);
            return r == LoginResult.Success;
        }
        catch { return false; }
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
                    Username = _config.Username,
                    Password = _config.Password,
                    IsPersistentSession = true,
                    Authenticator = new ConsoleAuthenticator()
                });

            var pollResult = await authSession.PollingWaitForResultAsync(token);

            _config.RefreshToken = pollResult.RefreshToken;
            _config.Save();
            Emit("✓ Session token saved — you won't need to enter an auth code again for ~1 month.");

            _loginTcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _steamUser?.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
                LoginID = (uint)Random.Shared.Next(1, 999999)
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
     * Reconnect logic
     */

    /// <summary>
    /// Called internally when we get a non-intentional disconnect.
    /// Waits 5 s, then re-connects and re-logs in using the saved refresh token.
    /// No user input needed as long as the refresh token is valid (~30 days).
    /// </summary>
    private void BeginAutoReconnect()
    {
        Task.Run(async () =>
        {
            for (int attempt = 1; _isRunning && !_pumpToken.IsCancellationRequested; attempt++)
            {
                int delaySec = Math.Min(30, 5 * attempt);
                Emit($"Reconnect attempt {attempt} in {delaySec}s...");
                await Task.Delay(TimeSpan.FromSeconds(delaySec), _pumpToken).ContinueWith(_ => { });

                if (!_isRunning || _pumpToken.IsCancellationRequested) break;

                InitClient();
                StartCallbackPump(_pumpToken);

                if (!await ConnectAsync(_pumpToken)) continue;
                if (!await LoginAsync(_pumpToken)) continue;

                Emit("✓ Reconnected successfully!");
                OnReconnected?.Invoke();
                return;
            }
        });
    }

    /*
     * Callbacks
     */

    private void OnConnected(SteamClient.ConnectedCallback _)
    {
        Emit("✓ Connected to Steam network");
        _connectTcs?.TrySetResult(true);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback _)
    {
        _isLoggedIn = false;
        Emit("Disconnected from Steam.");

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
            _isLoggedIn = true;
            LoggedInSteamID = cb.ClientSteamID;
            Emit($"✓ Logged on! SteamID: {cb.ClientSteamID}");
            _steamFriends?.SetPersonaState(EPersonaState.Online);
            _loginTcs?.TrySetResult(LoginResult.Success);
        }
        else
        {
            Emit($"Login failed: {cb.Result}");

            // If kicked by another device, fire special event
            if (cb.Result == EResult.LoggedInElsewhere)
            {
                Emit("⚠ Account logged in on another device!");
                OnLoggedInElsewhere?.Invoke();
            }

            var result = cb.Result switch
            {
                EResult.InvalidPassword => LoginResult.InvalidPassword,
                EResult.AccountLockedDown => LoginResult.AccountLocked,
                EResult.AccountLogonDenied => LoginResult.NeedsEmailCode,
                EResult.AccountLoginDeniedNeedTwoFactor => LoginResult.Needs2FA,
                EResult.TwoFactorCodeMismatch => LoginResult.Needs2FA,
                EResult.TwoFactorActivationCodeMismatch => LoginResult.Needs2FA,
                EResult.InvalidLoginAuthCode => LoginResult.NeedsEmailCode,
                _ => LoginResult.Unknown
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
            // Do NOT set intentionalDisconnect, let BeginAutoReconnect handle it
        }
    }

    private void OnFriendMessage(SteamFriends.FriendMsgCallback cb)
    {
        if (cb.EntryType != EChatEntryType.ChatMsg) return;
        if (string.IsNullOrWhiteSpace(cb.Message)) return;

        var name = _steamFriends?.GetFriendPersonaName(cb.Sender) ?? cb.Sender.ToString();
        var friendId = cb.Sender.ConvertToUInt64();
        var line = $"[{DateTime.Now:HH:mm:ss}] {name} ({friendId}): {cb.Message}";

        Emit($"[Chat] {line}");
        LogChat(line);

        // Fire event so GUI can update live
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
     * Helper
     */

    private static void LogChat(string line)
    {
        try { File.AppendAllText(ChatLogPath, line + Environment.NewLine); }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Write to console AND fire OnSystemEvent (for GUI).
    /// </summary>
    private void Emit(string msg)
    {
        Console.WriteLine(msg);
        OnSystemEvent?.Invoke(msg);
    }
}