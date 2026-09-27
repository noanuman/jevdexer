namespace Jevdexer.Core.Files;

/// <summary>
/// Where file search looks, and what it never enters. Every fixed drive, the system
/// drive included, so folders people make anywhere (C:\Projects, D:\Work) are found. Not: the
/// system, programs, app data, driver and vendor folders, dot-folders, developer build output,
/// version control, or the install folders of apps we already index as apps. OneDrive is added
/// when it lives outside the fixed drives (it is usually inside the profile, so already covered).
/// </summary>
public sealed class FileRoots
{
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Folder names skipped wherever they appear (case-insensitive).</summary>
    public static readonly IReadOnlySet<string> SkippedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows, programs, drivers and vendor tools
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "$WinREAgent",
        "System Volume Information", "Recovery", "PerfLogs", "AppData", "WindowsApps", "MSOCache", "Boot", "EFI",
        "Config.Msi", "Drivers", "Intel", "AMD", "NVIDIA", "OneDriveTemp", "inetpub", "ESD", "$SysReset", "$Windows.~BT", "$Windows.~WS",
        // developer output and tooling
        "tmp", "temp",
        "node_modules", "site-packages", "dist-packages", "bin", "obj", "__pycache__", ".venv", "venv", ".tox", ".gradle", ".m2", ".nuget",
        ".vs", ".idea", ".vscode", ".cache", ".npm", ".yarn", ".pnpm-store", ".next", ".turbo", "dist-newstyle",
        ".git", ".svn", ".hg",
        // game libraries (their games are apps; their assets are noise)
        "steamapps", "SteamLibrary", "Epic Games", "Riot Games",
    };

    private readonly HashSet<string> _skippedPaths;

    public FileRoots(IEnumerable<string> roots, IEnumerable<string> skippedPaths)
    {
        _skippedPaths = new HashSet<string>(skippedPaths.Select(Normalize).Where(p => p.Length > 3), StringComparer.OrdinalIgnoreCase);
        Roots = RemoveNested(roots.Select(Normalize).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Every fixed drive, plus OneDrive if it lives elsewhere.</summary>
    /// <param name="appInstallDirs">Install folders of indexed apps: never crawled.</param>
    public static FileRoots Default(IEnumerable<string> appInstallDirs)
    {
        var roots = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
        if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive) roots.Add(oneDrive);
        return new FileRoots(roots, appInstallDirs);
    }

    /// <summary>
    /// The folders the indexed apps live in, never crawled: the install folder, else the exe's own
    /// folder (Steam's uninstall entry names no InstallLocation, and a Steam folder is 50k files of
    /// UI assets). Never a drive root, a Windows or Program Files folder, or a user folder: a portable
    /// app in Downloads must not hide Downloads.
    /// </summary>
    public static IEnumerable<string> AppFolders(IEnumerable<IndexedApp> apps)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var protectedDirs = new[]
        {
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            profile, F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.MyDocuments), Path.Combine(profile, "Downloads"),
            F(Environment.SpecialFolder.MyPictures), F(Environment.SpecialFolder.MyVideos), F(Environment.SpecialFolder.MyMusic),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
        }.Where(d => d.Length > 0).Select(Normalize).ToList();

        // Protected: those folders, their ancestors (C:\Users), and drive roots.
        bool Protected(string dir) => dir.Length <= 3 || protectedDirs.Any(p =>
            p.Equals(dir, StringComparison.OrdinalIgnoreCase) || p.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase));

        foreach (var a in apps)
        {
            var dir = a.Entity.InstallDir is { Length: > 0 } install ? install : Path.GetDirectoryName(a.Entity.Exe ?? "");
            if (string.IsNullOrEmpty(dir)) continue;
            var normalized = Normalize(dir);
            if (!Protected(normalized)) yield return normalized;
        }
    }

    /// <summary>Whether the crawler may enter this folder.</summary>
    /// <remarks>Dot-folders (.ssh, .dotnet, .config) are tool state, not documents: skipped everywhere.</remarks>
    public bool ShouldEnter(string fullPath, string name) =>
        !SkippedNames.Contains(name) && !name.StartsWith('$') && !name.StartsWith('.') && !_skippedPaths.Contains(Normalize(fullPath));

    /// <summary>
    /// Whether the crawl would have reached this folder: under a root, and every folder on the way
    /// down passes <see cref="ShouldEnter"/> and is not hidden, system or a reparse point. Change
    /// notifications arrive for the whole drive (AppData, Temp, node_modules); this keeps skipped
    /// folders out of the index.
    /// </summary>
    /// <param name="checkAttributes">False: names only, no disk access (the watcher's cheap first filter).</param>
    public bool Covers(string folder, bool checkAttributes = true)
    {
        var path = Normalize(folder);
        var root = Roots.Where(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                                    && (path.Length == r.Length || r.EndsWith('\\') || path[r.Length] == '\\'))
            .MaxBy(r => r.Length);
        if (root is null) return false;

        var current = root.TrimEnd('\\');
        foreach (var name in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "\\" + name;
            if (!ShouldEnter(current, name)) return false;
            if (!checkAttributes) continue;
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Gone already (a delete): the names passed, so it was covered.
            }
        }
        return true;
    }

    public bool IsUnderRoot(string path) =>
        Roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                       && (path.Length == r.Length || r.EndsWith('\\') || path[r.Length] == '\\'));

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd('\\') : full; // keep "D:\"
    }

    /// <summary>Documents inside OneDrive is crawled once, as part of OneDrive.</summary>
    private static IEnumerable<string> RemoveNested(IEnumerable<string> roots)
    {
        var list = roots.OrderBy(r => r.Length).ToList();
        var kept = new List<string>();
        foreach (var r in list)
        {
            if (!kept.Any(k => r.StartsWith(k.EndsWith('\\') ? k : k + "\\", StringComparison.OrdinalIgnoreCase))) kept.Add(r);
        }
        return kept;
    }
}
