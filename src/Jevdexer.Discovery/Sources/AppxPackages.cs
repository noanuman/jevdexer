using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Jevdexer.Discovery.Sources;

/// <summary>
/// Store / MSIX packages for the current user, keyed by package family name. Not a source on
/// its own: a package is not an app (one package can hold several, or none). Used to add
/// publisher and location to AppsFolder entries.
/// </summary>
internal static class AppxPackages
{
    public sealed record Info(string? Publisher, string? Location);

    public static IReadOnlyDictionary<string, Info> Load()
    {
        var result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<Package> packages;
        try
        {
            packages = new PackageManager().FindPackagesForUser(string.Empty);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return result;
        }

        foreach (var p in packages)
        {
            try
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                result[p.Id.FamilyName] = new Info(
                    Publisher: Safe(() => p.PublisherDisplayName),
                    Location: Safe(() => p.InstalledLocation?.Path));
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or FileNotFoundException)
            {
                // A package can be mid-install or staged for another user; skip it.
            }
        }
        return result;
    }

    private static string? Safe(Func<string?> read)
    {
        try { return read(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or FileNotFoundException) { return null; }
    }
}
