using Microsoft.Win32;

namespace Jevdexer.Discovery.Sources;

/// <summary>
/// `...\CurrentVersion\Uninstall` in HKLM (64 and 32-bit views) and HKCU: what Settings
/// shows under Installed apps. Knows name, publisher and install dir, often not the exe.
/// </summary>
public sealed class UninstallKeysSource : IAppSource
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] SkippedReleaseTypes = ["Update", "Hotfix", "Security Update", "ServicePack"];

    public string Name => "uninstall";

    public IEnumerable<RawApp> Read()
    {
        (RegistryHive Hive, RegistryView View)[] roots =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        ];

        foreach (var (hive, view) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(KeyPath);
            if (uninstall is null) continue;

            foreach (var subName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(subName);
                var app = key is null ? null : ToApp(key, subName);
                if (app is not null) yield return app;
            }
        }
    }

    private static bool IsSetupExe(string? exe) =>
        exe is not null && (Path.GetFileName(exe).Contains("setup", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(exe).Contains("install", StringComparison.OrdinalIgnoreCase));

    private RawApp? ToApp(RegistryKey key, string subName)
    {
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name) || NotAnApp.ByName(name)) return null;
        if (key.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (key.GetValue("ParentKeyName") is string) return null;
        if (key.GetValue("ReleaseType") is string rt && SkippedReleaseTypes.Contains(rt)) return null;

        var iconSpec = key.GetValue("DisplayIcon") as string;
        var exe = PathUtil.ExeFromIconSpec(iconSpec);
        // DisplayIcon is an icon, not a launcher: it often names the setup exe (OneDriveSetup.exe, RobloxPlayerInstaller.exe).
        if (NotAnApp.ByExe(exe) || IsSetupExe(exe)) exe = null;

        return new RawApp
        {
            Source = Name,
            Name = name,
            Publisher = (key.GetValue("Publisher") as string)?.Trim(),
            Exe = exe,
            InstallDir = key.GetValue("InstallLocation") as string,
            IconPath = PathUtil.IconFileFromSpec(iconSpec),
            SourceId = subName,
        };
    }
}
