using System.Diagnostics;

namespace Jevdexer.Discovery;

/// <summary>Reads ProductName, FileDescription and CompanyName from an exe's PE version resource.</summary>
internal static class VersionReader
{
    public static VersionInfo? Read(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            var info = new VersionInfo(Clean(v.ProductName), Clean(v.FileDescription), Clean(v.CompanyName));
            return info is { ProductName: null, FileDescription: null, CompanyName: null } ? null : info;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
