using System.Text.Json;

namespace Jevdexer.Discovery.Sources;

/// <summary>
/// Epic Games Launcher installs: one JSON `.item` manifest per game under
/// `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests`, with the install folder and the exe.
/// </summary>
public sealed class EpicSource : IAppSource
{
    public string Name => "epic";

    public IEnumerable<RawApp> Read()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            var app = ReadItem(item);
            if (app is not null) yield return app;
        }
    }

    private RawApp? ReadItem(string path)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }

        var name = Str(root, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;

        var location = Str(root, "InstallLocation");
        var launch = Str(root, "LaunchExecutable");
        // Plugins and asset packs (Fab UE Plugin, Quixel Bridge) install into an engine folder and launch nothing.
        if (string.IsNullOrEmpty(launch)) return null;
        var appName = Str(root, "AppName");
        var ns = Str(root, "CatalogNamespace");
        var itemId = Str(root, "CatalogItemId");

        return new RawApp
        {
            Source = Name,
            Name = name,
            InstallDir = location,
            Exe = location is not null && !string.IsNullOrEmpty(launch) ? Path.Combine(location, launch) : null,
            LaunchUri = ns is not null && itemId is not null && appName is not null
                ? $"com.epicgames.launcher://apps/{ns}%3A{itemId}%3A{appName}?action=launch&silent=true"
                : null,
            SourceId = appName,
        };
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
