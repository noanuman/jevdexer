using Microsoft.Win32;

namespace Jevdexer.Discovery.Sources;

/// <summary>GOG Galaxy installs: one key per game under `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games`.</summary>
public sealed class GogSource : IAppSource
{
    public string Name => "gog";

    public IEnumerable<RawApp> Read()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (games is null) yield break;

        foreach (var id in games.GetSubKeyNames())
        {
            using var key = games.OpenSubKey(id);
            var name = key?.GetValue("gameName") as string ?? key?.GetValue("GAMENAME") as string;
            if (key is null || string.IsNullOrWhiteSpace(name)) continue;

            var dir = key.GetValue("path") as string ?? key.GetValue("PATH") as string;
            var exe = key.GetValue("exe") as string ?? key.GetValue("EXE") as string;
            if (exe is not null && !Path.IsPathFullyQualified(exe) && dir is not null) exe = Path.Combine(dir, exe);

            yield return new RawApp { Source = Name, Name = name, InstallDir = dir, Exe = exe, SourceId = id };
        }
    }
}
