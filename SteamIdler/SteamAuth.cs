using SteamKit2;
using SteamKit2.Authentication;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace SteamGameIdler;

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

    private volatile bool _isRunning;
    private volatile bool _intentionalDisconnect;
    private readonly HashSet<ulong> _repliedTo = new();

    public SteamID? LoggedInSteamID { get; private set; }
    public bool IsConnected => _steamClient?.IsConnected ?? false;
    public SteamUser? SteamUser => _steamUser;
    public SteamApps? SteamApps => _steamApps;
    public SteamFriends? SteamFriends => _steamFriends;

    public SteamAuth(AppConfig config)
    {
        _config = config;

        _steamClient  = new SteamClient();
        _manager      = new CallbackManager(_steamClient);
        _steamUser    = _steamClient.GetHandler<SteamUser>()!;
        _steamApps    = _steamClient.GetHandler<SteamApps>()!;
        _steamFriends = _steamClient.GetHandler<SteamFriends>()!;

        RegisterCallbacks();
    }

    private void RegisterCallbacks()
    {
        if (_manager == null) return;

        _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMessage);
    }
    
    public void Send(IClientMsg msg) => _steamClient?.Send(msg);
    
    public void StartCallbackPump(CancellationToken token)
    {
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
        catch (OperationCanceledException) { return false; }
    }
    
    public async Task<bool> LoginAsync(CancellationToken token = default)
    {
        if (_steamClient == null) return false;
        
        if (!string.IsNullOrWhiteSpace(_config.RefreshToken))
        {
            Console.WriteLine("Found saved session token — logging in automatically...");
            try
            {
                var result = await DoTokenLoginAsync(_config.RefreshToken, token);
                if (result)
                {
                    Console.WriteLine("✓ Logged in with saved token (no password needed)");
                    return true;
                }
                Console.WriteLine("Saved token was rejected — falling back to password login.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Token login failed ({ex.Message}) — falling back to password login.");
            }

            _config.RefreshToken = "";
            _config.Save();
        }
        return await DoPasswordLoginAsync(token);
    }
    
    private async Task<bool> DoTokenLoginAsync(string refreshToken, CancellationToken token)
    {
        _loginTcs = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _steamUser?.LogOn(new SteamUser.LogOnDetails
        {
            Username     = _config.Username,
            AccessToken  = refreshToken,
            LoginID      = (uint)Random.Shared.Next(1, 999999)
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            var result = await _loginTcs.Task.WaitAsync(cts.Token);
            return result == LoginResult.Success;
        }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<bool> DoPasswordLoginAsync(CancellationToken token)
    {
        if (_steamClient == null) return false;

        Console.WriteLine("Starting credential login...");

        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username        = _config.Username,
                    Password        = _config.Password,
                    IsPersistentSession = true,
                    Authenticator   = new ConsoleAuthenticator()
                });

            var pollResult = await authSession.PollingWaitForResultAsync(token);

            _config.RefreshToken = pollResult.RefreshToken;
            _config.Save();
            Console.WriteLine("✓ Session token saved — you won't be asked for an auth code again.");

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
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Login error: {ex.Message}");
            return false;
        }
    }
    
    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        Console.WriteLine("✓ Connected to Steam network");
        _connectTcs?.TrySetResult(true);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        Console.WriteLine("Disconnected from Steam");

        if (_intentionalDisconnect)
        {
            _isRunning = false;
            return;
        }

        _connectTcs?.TrySetResult(false);
        Console.WriteLine("Reconnecting in 5 seconds...");

        Task.Delay(5000).ContinueWith(_ =>
        {
            if (_isRunning)
            {
                _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _steamClient?.Connect();
            }
        });
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            LoggedInSteamID = callback.ClientSteamID;
            Console.WriteLine($"✓ Logged on! SteamID: {callback.ClientSteamID}");

            _steamFriends?.SetPersonaState(EPersonaState.Online);
            _loginTcs?.TrySetResult(LoginResult.Success);
        }
        else
        {
            Console.WriteLine($"Login failed: {callback.Result}");

            LoginResult result = callback.Result switch
            {
                EResult.InvalidPassword                 => LoginResult.InvalidPassword,
                EResult.AccountLockedDown               => LoginResult.AccountLocked,
                EResult.AccountLogonDenied              => LoginResult.NeedsEmailCode,
                EResult.AccountLoginDeniedNeedTwoFactor => LoginResult.Needs2FA,
                EResult.TwoFactorCodeMismatch           => LoginResult.Needs2FA,
                EResult.TwoFactorActivationCodeMismatch => LoginResult.Needs2FA,
                EResult.InvalidLoginAuthCode            => LoginResult.NeedsEmailCode,
                _                                       => LoginResult.Unknown
            };

            _loginTcs?.TrySetResult(result);
        }
    }
    
    private void OnFriendMessage(SteamFriends.FriendMsgCallback callback)
    {
        if (callback.EntryType != EChatEntryType.ChatMsg)
            return;

        if (string.IsNullOrWhiteSpace(callback.Message))
            return;

        var friendId = callback.Sender.ConvertToUInt64();

        Console.WriteLine($"[Chat] {callback.Sender}: {callback.Message}");

        if (!_config.AutoReplyEnabled)
            return;
        if (_repliedTo.Contains(friendId))
            return;

        _repliedTo.Add(friendId);

        var reply = string.IsNullOrWhiteSpace(_config.AutoReplyMessage)
            ? "I'm currently idling games and can't chat right now."
            : _config.AutoReplyMessage;

        _steamFriends?.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, reply);
        Console.WriteLine($"[Auto-reply sent to {callback.Sender}]: {reply}");
    }
    
    public void Stop()
    {
        _intentionalDisconnect = true;
        _isRunning = false;
        _steamUser?.LogOff();
        _steamClient?.Disconnect();
    }
}