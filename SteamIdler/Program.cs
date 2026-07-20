using SteamGameIdler.Shared;
using System.Text;

namespace SteamGameIdler.CLI;

class Program
{
    private static SteamAuth? _steamAuth;
    private static GameIdler? _gameIdler;
    private static CancellationTokenSource? _cts;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Steam Game Idler";

        Console.WriteLine(@"
╔═══════════════════════════════════════════╗
║         Steam Game Idler v2.1             ║
║          Author: SyntX                    ║
║     Automatically idle Steam games        ║
╚═══════════════════════════════════════════╝
");

        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts?.Cancel();
            Console.WriteLine("\n\nShutting down gracefully...");
        };

        try
        {
            await RunIdler(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Program stopped by user.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    static async Task RunIdler(CancellationToken token)
    {
        var config = AppConfig.LoadOrCreate();

        if (!config.IsComplete())
        {
            Console.WriteLine("\nPlease edit config.json with your Steam username and password, then restart.");
            return;
        }

        // ------------------------------------------------------------------
        // Load games.txt
        // ------------------------------------------------------------------
        const string gamesFile = "games.txt";

        if (!File.Exists(gamesFile))
        {
            await File.WriteAllTextAsync(gamesFile,
                "// Add one Steam AppID per line. Lines starting with // are ignored.\n" +
                "// Example:\n" +
                "// 730    // CS2\n" +
                "// 570    // Dota 2\n" +
                "// 440    // Team Fortress 2\n", token);

            Console.WriteLine($"'{gamesFile}' has been created. Add your game AppIDs and restart.");
            return;
        }

        var gameIds = new List<uint>();
        foreach (var line in await File.ReadAllLinesAsync(gamesFile, token))
        {
            var clean = line.Split("//")[0].Trim();
            if (uint.TryParse(clean, out uint appId) && appId > 0)
                gameIds.Add(appId);
        }

        if (gameIds.Count == 0)
        {
            Console.WriteLine($"No valid AppIDs found in '{gamesFile}'. Add some and restart.");
            return;
        }

        Console.WriteLine($"Loaded {gameIds.Count} game(s) to idle:");
        foreach (var id in gameIds)
            Console.WriteLine($"  AppID {id}");
        Console.WriteLine();

        // ------------------------------------------------------------------
        // Connect & login
        // ------------------------------------------------------------------
        _steamAuth = new SteamAuth(config);

        // Wire up special events
        _steamAuth.OnLoggedInElsewhere += () =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n⚠ WARNING: Your account was logged in on another device!");
            Console.WriteLine("  The idler will attempt to reclaim the session automatically.");
            Console.ResetColor();
        };

        _steamAuth.StartCallbackPump(token);

        if (!await _steamAuth.ConnectAsync(token))
        {
            Console.WriteLine("Could not connect to Steam. Check your internet connection.");
            return;
        }

        if (!await _steamAuth.LoginAsync(token))
        {
            Console.WriteLine("Login failed. Check your username/password in config.json.");
            return;
        }

        Console.WriteLine($"\n✓ Authenticated successfully!");
        Console.WriteLine($"  Logged in as: {_steamAuth.LoggedInSteamID}");

        if (config.AutoReplyEnabled)
            Console.WriteLine($"  Auto-reply: ON — \"{config.AutoReplyMessage}\"");
        else
            Console.WriteLine("  Auto-reply: OFF");

        Console.WriteLine();
        Console.WriteLine("  Type  'msg <SteamID64> <message>'  to send a chat message.");
        Console.WriteLine("  Type  'friends'                    to list your friends.");
        Console.WriteLine("  Type  'quit'                       to stop.\n");

        // ------------------------------------------------------------------
        // Start console command listener in background
        // ------------------------------------------------------------------
        _ = Task.Run(() => ConsoleCommandLoop(token), token);

        // ------------------------------------------------------------------
        // Start idling
        // ------------------------------------------------------------------
        _gameIdler = new GameIdler(_steamAuth, config);
        await _gameIdler.StartIdling(gameIds, token);
    }

    // -----------------------------------------------------------------------
    // Console command loop — runs in a background thread so it doesn't block
    // idling. Very simple: "msg <id64> <text>" and "friends".
    // -----------------------------------------------------------------------
    static void ConsoleCommandLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;

            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                _cts?.Cancel();
                return;
            }

            if (input.Equals("friends", StringComparison.OrdinalIgnoreCase))
            {
                if (_steamAuth == null) { Console.WriteLine("Not connected."); continue; }
                var friends = _steamAuth.GetFriendList();
                if (friends.Count == 0) { Console.WriteLine("No friends found (or list not loaded yet)."); continue; }
                Console.WriteLine("\n--- Friends ---");
                foreach (var (id, name, state) in friends)
                    Console.WriteLine($"  {name,-30} {id}  [{state}]");
                Console.WriteLine("---------------\n");
                continue;
            }

            // msg <steamid64> <message text>
            if (input.StartsWith("msg ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input.Substring(4).Trim().Split(' ', 2);
                if (parts.Length < 2 || !ulong.TryParse(parts[0], out var sid64))
                {
                    Console.WriteLine("Usage: msg <SteamID64> <message>");
                    continue;
                }

                if (_steamAuth == null) { Console.WriteLine("Not connected."); continue; }

                bool ok = _steamAuth.SendMessage(sid64, parts[1]);
                Console.WriteLine(ok ? "✓ Message sent." : "✗ Failed to send (not logged in?).");
                continue;
            }

            Console.WriteLine("Unknown command. Available: msg <id64> <text>, friends, quit");
        }
    }
}