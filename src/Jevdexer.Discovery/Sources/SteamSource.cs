using Microsoft.Win32;

namespace Jevdexer.Discovery.Sources;

/// <summary>
/// Steam games in every library, on any drive: `libraryfolders.vdf` lists the libraries, and
/// each `appmanifest_&lt;appid&gt;.acf` gives the name and install folder. The exe is not
/// recorded; games launch through `steam://rungameid/&lt;appid&gt;`.
/// </summary>
public sealed class SteamSource : IAppSource
{
    /// <summary>Installed by Steam itself, never launched by a person.</summary>
    private static readonly HashSet<string> SkippedAppIds = ["228980"]; // Steamworks Common Redistributables

    public string Name => "steam";

    public IEnumerable<RawApp> Read()
    {
        var steamRoot = FindSteamRoot();
        if (steamRoot is null) yield break;

        foreach (var library in Libraries(steamRoot))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var app = ReadManifest(acf, steamapps);
                if (app is not null) yield return app;
            }
        }
    }

    private static string? FindSteamRoot()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return path is not null && Directory.Exists(path) ? path.Replace('/', '\\') : null;
    }

    private static IEnumerable<string> Libraries(string steamRoot)
    {
        var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return libs;

        var root = Vdf.Parse(File.ReadAllText(vdf)).Child("libraryfolders");
        foreach (var lib in root?.Children.Values ?? Enumerable.Empty<Vdf.Node>())
        {
            if (lib.Get("path") is { } p) libs.Add(p);
        }
        return libs;
    }

    private RawApp? ReadManifest(string acfPath, string steamapps)
    {
        Vdf.Node? state;
        try
        {
            state = Vdf.Parse(File.ReadAllText(acfPath)).Child("AppState");
        }
        catch (IOException)
        {
            return null; // Steam holds the file open while updating.
        }

        var appId = state?.Get("appid");
        var name = state?.Get("name");
        if (appId is null || string.IsNullOrWhiteSpace(name) || SkippedAppIds.Contains(appId)) return null;

        var installDir = state!.Get("installdir");
        return new RawApp
        {
            Source = Name,
            Name = name,
            InstallDir = installDir is null ? null : Path.Combine(steamapps, "common", installDir),
            LaunchUri = $"steam://rungameid/{appId}",
            SourceId = appId,
        };
    }
}
