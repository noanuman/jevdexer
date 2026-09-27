namespace Jevdexer.Discovery;

/// <summary>One sighting of an app by one source, before merging.</summary>
public sealed class RawApp
{
    public required string Source { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }

    /// <summary>Full path to the launchable exe, when the source knows it.</summary>
    public string? Exe { get; init; }

    /// <summary>
    /// Shortcut arguments. Launcher shortcuts share one exe and differ only here
    /// (`RiotClientServices.exe --launch-product=league_of_legends`).
    /// </summary>
    public string? Args { get; init; }

    /// <summary>AppUserModelID, for Store apps and desktop apps that register one.</summary>
    public string? Aumid { get; init; }

    public string? InstallDir { get; init; }

    /// <summary>An icon file the source names (Uninstall `DisplayIcon`), for apps with no exe to take one from.</summary>
    public string? IconPath { get; init; }

    /// <summary>A launch URI when the app is started through its launcher (steam://, com.epicgames.launcher://).</summary>
    public string? LaunchUri { get; init; }

    /// <summary>Source-specific identifier (Steam appid, Epic AppName, GOG id), for dedupe and enrichment.</summary>
    public string? SourceId { get; init; }
}
