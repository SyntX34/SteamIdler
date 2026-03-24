using SteamKit2;

namespace SteamGameIdler;

public class GameIdler
{
    private readonly SteamAuth _steamAuth;
    private readonly Dictionary<uint, DateTime> _gameStartTimes = new();
    private volatile bool _isIdling;

    public GameIdler(SteamAuth steamAuth)
    {
        _steamAuth = steamAuth;
    }

    public async Task StartIdling(List<uint> gameIds, CancellationToken token)
    {
        if (!_steamAuth.IsConnected)
            throw new InvalidOperationException("Not connected to Steam");

        _isIdling = true;

        Console.WriteLine($"Starting to idle {gameIds.Count} game(s) simultaneously...");
        Console.WriteLine("Press Ctrl+C to stop\n");

        foreach (var id in gameIds)
            _gameStartTimes[id] = DateTime.Now;

        SetPlayingGames(gameIds);

        var statsTimer = DateTime.Now;

        try
        {
            while (_isIdling && !token.IsCancellationRequested)
            {
                if ((DateTime.Now - statsTimer).TotalHours >= 1)
                {
                    DisplayStats();
                    statsTimer = DateTime.Now;
                }

                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C
        }
        finally
        {
            _isIdling = false;
            StopAllGames();
            DisplayStats();
            _steamAuth.Stop();
        }
    }

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
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Idling: AppID {appId}");
        }

        _steamAuth.Send(msg);
    }

    private void StopAllGames()
    {
        Console.WriteLine("\nStopping all games...");

        var msg = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(
            EMsg.ClientGamesPlayed);

        _steamAuth.Send(msg);
    }

    private void DisplayStats()
    {
        if (_gameStartTimes.Count == 0) return;

        Console.WriteLine("\n=== Idling Statistics ===");
        foreach (var (appId, startTime) in _gameStartTimes)
        {
            var duration = DateTime.Now - startTime;
            Console.WriteLine($"  AppID {appId,8}: {(int)duration.TotalHours:D2}h {duration.Minutes:D2}m {duration.Seconds:D2}s");
        }
        Console.WriteLine("=========================\n");
    }
}