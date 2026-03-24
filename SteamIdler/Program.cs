using System.Text;

namespace SteamGameIdler;

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
║         Steam Game Idler v2.0             ║
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

        _steamAuth = new SteamAuth(config);
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
        _gameIdler = new GameIdler(_steamAuth);
        await _gameIdler.StartIdling(gameIds, token);
    }
}