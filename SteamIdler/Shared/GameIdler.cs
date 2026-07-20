using SteamKit2;

namespace SteamGameIdler.Shared;

public class GameIdler
{
    private readonly SteamAuth _steamAuth;
    private readonly AppConfig _config;
    private readonly Dictionary<uint, DateTime> _gameStartTimes = new();
    private volatile bool _isIdling;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private double _totalIdleHoursThisSession;

    // Games currently being played
    // so we can re-set them after a reconnect
    private List<uint> _currentGameIds = new();

    public event Action<string>? OnStatusUpdate;

    public GameIdler(SteamAuth steamAuth, AppConfig config)
    {
        _steamAuth = steamAuth;
        _config = config;

        // When SteamAuth reconnects automatically
        // re-set the games playing
        _steamAuth.OnReconnected += () =>
        {
            if (_isIdling && _currentGameIds.Count > 0 && DateTime.Now >= _cooldownUntil)
            {
                Emit("Re-setting games after reconnect...");
                SetPlayingGames(_currentGameIds);
            }
        };
    }

    public async Task StartIdling(List<uint> gameIds, CancellationToken token)
    {
        if (!_steamAuth.IsConnected)
            throw new InvalidOperationException("Not connected to Steam");

        _isIdling = true;
        _currentGameIds = gameIds;
        _totalIdleHoursThisSession = 0;
        _cooldownUntil = DateTime.MinValue;

        Emit($"Starting to idle {gameIds.Count} game(s) simultaneously...");

        double maxHours = _config.MaxIdleHours;
        Emit($"Max idle time: {maxHours}h before {_config.CooldownMinutes}min cooldown\n");

        var sessionStart = DateTime.Now;
        foreach (var id in gameIds)
            _gameStartTimes[id] = DateTime.Now;

        SetPlayingGames(gameIds);

        // Fetch and show achievements for each game on startup
        await ShowAchievementsAsync(gameIds);

        var statsTimer = DateTime.Now;
        var achTimer = DateTime.Now;

        try
        {
            while (_isIdling && !token.IsCancellationRequested && !_steamAuth.IsSessionExpired)
            {
                /*
                 * Cooldown check
                */
                if (_cooldownUntil > DateTime.Now)
                {
                    var remaining = _cooldownUntil - DateTime.Now;
                    Emit($"⏳ Cooldown active — {remaining.Minutes:D2}m {remaining.Seconds:D2}s remaining");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), token);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // When cooldown ends, resume idling
                if (_cooldownUntil != DateTime.MinValue && DateTime.Now >= _cooldownUntil)
                {
                    Emit("\n✓ Cooldown period over — resuming idle!");
                    _cooldownUntil = DateTime.MinValue;
                    _totalIdleHoursThisSession = 0;
                    sessionStart = DateTime.Now;
                    foreach (var id in gameIds)
                        _gameStartTimes[id] = DateTime.Now;
                    SetPlayingGames(gameIds);
                    await ShowAchievementsAsync(gameIds);
                }

                // Display stats every hour
                if ((DateTime.Now - statsTimer).TotalHours >= 1)
                {
                    DisplayStats();
                    statsTimer = DateTime.Now;
                }

                // recheck on every 30 minutes.
                if ((DateTime.Now - achTimer).TotalMinutes >= 30)
                {
                    await ShowAchievementsAsync(gameIds);
                    achTimer = DateTime.Now;
                }

                /*
                 * Check:
                 * if we've hit max idle hours 
                */
                _totalIdleHoursThisSession = (DateTime.Now - sessionStart).TotalHours;
                if (_totalIdleHoursThisSession >= maxHours)
                {
                    double cooldownMin = _config.CooldownMinutes;
                    Emit($"\n⚠ Reached maximum idle time ({maxHours:F1}h). Starting {cooldownMin}min cooldown...");
                    StopAllGames();
                    _cooldownUntil = DateTime.Now.AddMinutes(cooldownMin);
                    Emit($"Cooldown ends at: {_cooldownUntil:HH:mm:ss}\n");
                    DisplayStats();
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isIdling = false;
            StopAllGames();
            DisplayStats();
            _steamAuth.Stop();
        }
    }

    /*
     * Games
     */

    private void SetPlayingGames(IEnumerable<uint> appIds)
    {
        var msg = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(
            EMsg.ClientGamesPlayed);

        foreach (var appId in appIds)
        {
            msg.Body.games_played.Add(new SteamKit2.Internal.CMsgClientGamesPlayed.GamePlayed
            {
                game_id = appId
            });
            Emit($"[{DateTime.Now:HH:mm:ss}] Idling: AppID {appId}");
        }

        _steamAuth.Send(msg);
    }

    private void StopAllGames()
    {
        Emit("\nStopping all games...");
        var msg = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(
            EMsg.ClientGamesPlayed);
        _steamAuth.Send(msg);
    }

    /*
     * Achievements
     */

    private async Task ShowAchievementsAsync(List<uint> appIds)
    {
        if (_steamAuth.SteamApps == null || _steamAuth.LoggedInSteamID == null) return;

        Emit("\n=== Achievement Status ===");

        foreach (var appId in appIds)
        {
            try
            {
                var handler = _steamAuth.SteamApps;

                // Request AppInfo to at least confirm the game exists
                // get its name
                var request = new SteamApps.PICSRequest
                {
                    ID = appId
                };

                var infoRequest = await handler!.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, new List<SteamApps.PICSRequest>());

                if (infoRequest.Results == null) continue;

                foreach (var result in infoRequest.Results)
                {
                    foreach (var app in result.Apps)
                    {
                        var kv = app.Value.KeyValues;
                        var name = kv["common"]["name"].Value ?? $"AppID {appId}";
                        Emit($"  [{appId}] {name}");

                        // Hours idled this session / limitations.
                        if (_gameStartTimes.TryGetValue(appId, out var start))
                        {
                            var hrs = (DateTime.Now - start).TotalHours;
                            Emit($"       Session time: {hrs:F1}h idled");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Emit($"  AppID {appId}: Could not fetch info ({ex.Message})");
            }
        }

        Emit("=========================\n");
    }

    /*
     * Stats
     */

    private void DisplayStats()
    {
        if (_gameStartTimes.Count == 0) return;

        Emit("\n=== Idling Statistics ===");
        foreach (var (appId, startTime) in _gameStartTimes)
        {
            var duration = DateTime.Now - startTime;
            Emit($"  AppID {appId,8}: {(int)duration.TotalHours:D2}h {duration.Minutes:D2}m {duration.Seconds:D2}s");
        }
        Emit("=========================\n");
    }

    private void Emit(string msg)
    {
        Console.WriteLine(msg);
        OnStatusUpdate?.Invoke(msg);
    }
}