using Jevdexer.Discovery.Interop;

namespace Jevdexer.Discovery.Sources;

/// <summary>
/// .lnk files in the Start Menu (per-user and all-users) and on both Desktops. The shortcut
/// name is what the user sees; the target is the real exe, wherever it was installed.
/// </summary>
public sealed class ShortcutsSource : IAppSource
{
    public string Name => "shortcut";

    public IEnumerable<RawApp> Read()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ];

        foreach (var root in roots.Where(Directory.Exists))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                var app = ToApp(lnk);
                if (app is not null) yield return app;
            }
        }
    }

    private RawApp? ToApp(string lnk)
    {
        var name = Path.GetFileNameWithoutExtension(lnk);
        if (NotAnApp.ByName(name)) return null;

        var (target, args) = ShellLink.Read(lnk);
        if (target is null || NotAnApp.ByTarget(target)) return null;
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || NotAnApp.ByExe(target)) return null;

        return new RawApp { Source = Name, Name = name, Exe = target, Args = args };
    }
}
