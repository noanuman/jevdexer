using Jevdexer.Discovery.Interop;

namespace Jevdexer.Discovery.Sources;

/// <summary>
/// `shell:AppsFolder`, the Start menu's own "All apps" list: every launchable app for this
/// user, Store and desktop alike, under the name Start shows. Store apps carry an AUMID
/// (`Family!AppId`); publisher and location come from <see cref="AppxPackages"/>.
/// Must run on an STA thread (Shell.Application is apartment-threaded).
/// </summary>
public sealed class AppsFolderSource : IAppSource
{
    public string Name => "appsfolder";

    public IEnumerable<RawApp> Read()
    {
        var packages = AppxPackages.Load();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) yield break;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? folder = shell.NameSpace("shell:AppsFolder");
        if (folder is null) yield break;

        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string path = item.Path;
            var app = ToApp(name, path, packages);
            if (app is not null) yield return app;
        }
    }

    private RawApp? ToApp(string name, string path, IReadOnlyDictionary<string, AppxPackages.Info> packages)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || NotAnApp.ByName(name)) return null;

        // Store app: PackageFamilyName!AppId
        var bang = path.IndexOf('!');
        if (bang > 0 && !path.Contains('\\'))
        {
            packages.TryGetValue(path[..bang], out var pkg);
            return new RawApp
            {
                Source = Name, Name = name, Aumid = path,
                Publisher = pkg?.Publisher, InstallDir = pkg?.Location,
            };
        }

        var expanded = KnownFolders.Expand(path);
        if (expanded is null || NotAnApp.ByTarget(expanded)) return null;

        if (Path.IsPathFullyQualified(expanded))
        {
            if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || NotAnApp.ByExe(expanded)) return null;
            return new RawApp { Source = Name, Name = name, Exe = expanded };
        }

        // A desktop app that registered its own AUMID (e.g. "Chrome", "Microsoft.Office.WINWORD.EXE.15").
        return new RawApp { Source = Name, Name = name, Aumid = path };
    }
}
