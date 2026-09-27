using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Jevdexer.Discovery;

namespace Jevdexer.App;

/// <summary>Starts an entity the way its source says it should be started.</summary>
internal static class Launcher
{
    /// <summary>
    /// Order: a launcher's own URI for launcher games (Steam, Epic: the game may need its launcher
    /// for DRM or updates); then the AUMID (Store apps and desktop apps that registered one);
    /// then the exe with its shortcut arguments (League of Legends is RiotClientServices.exe
    /// plus arguments); then any URI.
    /// </summary>
    public static bool Launch(Entity e, out string? error)
    {
        error = null;
        try
        {
            if (e.LaunchUri is not null && (e.Sources.Contains("steam") || e.Sources.Contains("epic"))) Shell(e.LaunchUri);
            else if (e.Aumid is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{e.Aumid}") { UseShellExecute = false });
            else if (e.Exe is not null && File.Exists(e.Exe))
                Process.Start(new ProcessStartInfo(e.Exe, e.Args ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(e.Exe)! });
            else if (e.LaunchUri is not null) Shell(e.LaunchUri);
            else
            {
                error = $"{e.Name} has no way to start it";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            error = $"Could not start {e.Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Opens a file with its default app or a folder in Explorer. <paramref name="reveal"/>: open the
    /// containing folder with the item selected instead.
    /// </summary>
    public static bool OpenPath(string path, bool isFolder, bool reveal, out string? error)
    {
        error = null;
        if (!(isFolder ? Directory.Exists(path) : File.Exists(path)))
        {
            error = $"{Path.GetFileName(path)} is no longer there";
            return false;
        }
        try
        {
            if (reveal) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else Shell(path);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    private static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
