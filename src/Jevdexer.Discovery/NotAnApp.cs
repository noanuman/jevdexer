namespace Jevdexer.Discovery;

/// <summary>
/// Drops sightings that are certainly not something a person launches: uninstallers,
/// readmes, web links. Only the unambiguous cases. Anything subtler (helpers, overlays,
/// redistributables) is left for labeling's is_background_component question.
/// </summary>
internal static class NotAnApp
{
    private static readonly string[] NameWords =
        ["uninstall", "readme", "read me", "release notes", "license", "licence", "manual", "website", "help"];

    private static readonly string[] ExePrefixes = ["unins", "uninst", "uninstall"];

    private static readonly string[] NonExeTargets =
        [".url", ".txt", ".pdf", ".htm", ".html", ".chm", ".rtf", ".md", ".ini", ".log"];

    public static bool ByName(string name) =>
        NameWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Installer caches: an Uninstall key's DisplayIcon often points at the cached setup exe.</summary>
    private static readonly string[] InstallerCacheDirs = [@"\package cache\", @"\windows\installer\"];

    public static bool ByExe(string? exe)
    {
        if (exe is null) return false;
        var file = Path.GetFileName(exe);
        return ExePrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase))
               || InstallerCacheDirs.Any(d => exe.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ByTarget(string target) =>
        NonExeTargets.Any(e => target.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
