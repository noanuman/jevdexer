using System.IO;
using Jevdexer.Core;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;

namespace Jevdexer.App;

/// <summary>
/// What being an installed app means: start with Windows, clean removal, and updates.
///
/// Installed by Velopack, per user, no admin: the program lives in `%LOCALAPPDATA%\Jevdexer.App`
/// (Velopack's folder, replaced on update) and the user's data in `%LOCALAPPDATA%\Jevdexer`
/// (ours, untouched by updates). A dev build (`bin\Debug`) is not installed: it never writes the
/// startup entry and never updates itself.
/// </summary>
internal static class Installation
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Jevdexer";

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null;
            }
            catch (Exception)
            {
                return false; // no locator for this platform or build: treat as a dev copy
            }
        }
    }

    public static string Version =>
        (IsInstalled ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString() : null)
        ?? typeof(Installation).Assembly.GetName().Version?.ToString(3) ?? "dev";

    // ---------------------------------------------------------------- start with Windows

    /// <summary>Makes the Run entry match the setting. No-op for a dev build.</summary>
    public static void SyncStartup(bool enabled)
    {
        if (!IsInstalled) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(RunValue, $"\"{exe}\" --background");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    // ---------------------------------------------------------------- Velopack hooks (no UI, run and exit)

    public static void OnInstalled()
    {
        // First install: start with Windows unless a previous install turned it off.
        SyncStartup(Settings.Load().StartWithWindows);
    }

    /// <summary>
    /// Uninstalling removes everything Jevdexer kept about this user: the index, search history,
    /// settings (with the saved key), the crash log, Ultra Search's content database and models
    /// (all under the data folder), and the content worker's AppContainer profile.
    /// </summary>
    public static void OnUninstalling()
    {
        try
        {
            Jevdexer.Core.Ultra.Sandbox.DeleteProfile();
        }
        catch (Exception) { /* best effort */ }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception) { /* best effort: uninstall must not fail on this */ }

        try
        {
            if (Directory.Exists(Settings.DataDir)) Directory.Delete(Settings.DataDir, recursive: true);
        }
        catch (Exception) { /* files in use: Windows removes them when the app exits */ }
    }

    // ---------------------------------------------------------------- updates

    /// <summary>
    /// Checks the update feed and downloads a newer version in the background; Velopack applies it
    /// on the next start. Returns the version downloaded, or null. Never throws.
    /// </summary>
    public static async Task<string?> DownloadUpdateAsync(string? feedUrl)
    {
        if (!IsInstalled || string.IsNullOrWhiteSpace(feedUrl)) return null;
        try
        {
            var manager = new UpdateManager(feedUrl);
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return null;
            await manager.DownloadUpdatesAsync(update);
            return update.TargetFullRelease.Version.ToString();
        }
        catch (Exception)
        {
            return null; // offline or feed down: try again next start
        }
    }
}
