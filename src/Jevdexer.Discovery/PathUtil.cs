namespace Jevdexer.Discovery;

internal static class PathUtil
{
    /// <summary>Full, lower-case, no trailing separator. Null for anything that is not a rooted local path.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (!Path.IsPathFullyQualified(p)) return null;
        try
        {
            return Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant();
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>`DisplayIcon` values look like `C:\x\app.exe,0`. Returns the exe part, if it is an exe.</summary>
    public static string? ExeFromIconSpec(string? icon) => FileFromIconSpec(icon, ".exe");

    /// <summary>`DisplayIcon` pointing at an .ico file (Steam games), without its index.</summary>
    public static string? IconFileFromSpec(string? icon) => FileFromIconSpec(icon, ".ico");

    private static string? FileFromIconSpec(string? icon, string extension)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        if (comma > 0 && !s[(comma + 1)..].Contains('\\')) s = s[..comma].Trim('"');
        return s.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="dir"/>. Both normalized.</summary>
    public static bool IsUnder(string path, string dir) =>
        path.Length > dir.Length && path.StartsWith(dir, StringComparison.Ordinal) && path[dir.Length] == '\\';

    public static int Depth(string path) => path.Count(c => c == '\\');
}
