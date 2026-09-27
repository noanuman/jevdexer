using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jevdexer.Core;

/// <summary>
/// `%LOCALAPPDATA%\Jevdexer\settings.json`. The user's Vercel AI Gateway key is stored
/// DPAPI-protected; Smart search calls Jev with it directly.
/// `JEVDEXER_GATEWAY_KEY` overrides it for this process only (development) and is never written.
/// </summary>
public sealed class Settings
{
    private string? _keyOverride;
    private string _key = "";

    /// <summary>The user's own AI Gateway key; empty: Smart search has nothing to call and stays local.</summary>
    [JsonIgnore]
    public string GatewayKey
    {
        get => _keyOverride ?? _key;
        set => _key = value.Trim();
    }

    /// <summary>What is written to disk: always protected.</summary>
    [JsonPropertyName("gatewayKey")]
    public string StoredGatewayKey
    {
        get => _key.Length == 0 ? "" : Protector.Protect(_key);
        set => _key = value.Length == 0 ? "" : Protector.Unprotect(value);
    }

    /// <summary>Smart search can run: it is on and there is a key to call Jev with.</summary>
    [JsonIgnore]
    public bool JevOn => SmartSearch && GatewayKey.Length > 0;

    /// <summary>Modifier names joined by '+', then the key: "Win+Alt+Space".</summary>
    public string Hotkey { get; set; } = "Win+Alt+Space";

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// When off (or without a key), nothing leaves the PC: no labeling, no query-time Jev. Search by
    /// name, stored labels and Ultra Search's local index keeps working.
    /// </summary>
    public bool SmartSearch { get; set; } = true;

    /// <summary>
    /// Opt-in, off by default: a descriptive folder search sends up to 120 scrubbed folder summaries
    /// (name, two parent names, file types, a few file names, dates) to Jev. Needs <see cref="SmartSearch"/> on.
    /// </summary>
    public bool SmartFolders { get; set; }

    /// <summary>Ultra Search: the folders the user picked, the mode and the spending cap.</summary>
    [JsonIgnore]
    public Jevdexer.Core.Ultra.UltraSettings Ultra { get; set; } = new();

    /// <summary>What is written to disk: protected, since folder paths are personal.</summary>
    [JsonPropertyName("ultra")]
    public string StoredUltra
    {
        get => Protector.Protect(JsonSerializer.Serialize(Ultra, Json));
        set => Ultra = JsonSerializer.Deserialize<Jevdexer.Core.Ultra.UltraSettings>(Protector.Unprotect(value), Json) ?? new();
    }

    /// <summary>The first-run tour was finished or skipped (App: TourWindow).</summary>
    public bool TourDone { get; set; }

    /// <summary>Start Jevdexer when the user signs in. Applied only by an installed copy (App: Installation.SyncStartup).</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Velopack release feed. Empty: no automatic updates.</summary>
    public string? UpdateUrl { get; set; } = "https://jevdexer-releases.vercel.app";

    /// <summary>Where the user dragged the search window (device-independent pixels). Null: centred near the top.</summary>
    public double? SearchLeft { get; set; }
    public double? SearchTop { get; set; }

    /// <summary>`%LOCALAPPDATA%\Jevdexer`, or `JEVDEXER_DATA_DIR` (development: a throwaway profile).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("JEVDEXER_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jevdexer");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            // A broken file, or one protected by another Windows user: start fresh rather than stop search.
            s = new();
        }
        if (Environment.GetEnvironmentVariable("JEVDEXER_GATEWAY_KEY") is { Length: > 0 } key) s._keyOverride = key;
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
