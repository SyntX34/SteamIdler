using Newtonsoft.Json;

namespace SteamGameIdler.Shared;

public class AppConfig
{
    [JsonProperty("username")]
    public string Username { get; set; } = "";

    [JsonProperty("password")]
    public string Password { get; set; } = "";

    [JsonProperty("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonProperty("auto_reply_message")]
    public string AutoReplyMessage { get; set; } =
        "Hey! I'm currently idling Steam games and can't chat right now. I'll get back to you later!";

    [JsonProperty("auto_reply_enabled")]
    public bool AutoReplyEnabled { get; set; } = true;

    [JsonProperty("max_idle_hours")]
    public double MaxIdleHours { get; set; } = 10.0;

    [JsonProperty("cooldown_minutes")]
    public double CooldownMinutes { get; set; } = 30.0;

    private static readonly string _path = "config.json";

    public static AppConfig LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            try
            {
                var existing = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(_path));
                if (existing != null)
                {
                    Console.WriteLine("✓ Loaded config.json");
                    return existing;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not read config.json ({ex.Message}), recreating...");
            }
        }

        var defaults = new AppConfig();
        Save(defaults);

        Console.WriteLine("╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║  config.json has been created in this folder.        ║");
        Console.WriteLine("║  Please fill in your username and password, then     ║");
        Console.WriteLine("║  restart the program.                                ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝");
        return defaults;
    }

    public static void Save(AppConfig cfg)
    {
        File.WriteAllText(_path, JsonConvert.SerializeObject(cfg, Formatting.Indented));
    }

    public void Save() => Save(this);

    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}