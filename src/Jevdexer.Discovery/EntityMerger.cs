using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jevdexer.Discovery;

/// <summary>
/// Merges sightings from all sources into entities. Two sightings are the same app when they
/// share an exe under the same name, an AUMID, a launcher id, a specific install folder, or a
/// normalized name; and exe sightings join the install-folder sighting they live under when
/// they are the only app there (one exe, one name). So "Git Bash" and "Git GUI" stay two
/// entities, a game's one shortcut joins its launcher record, and League of Legends does not
/// merge into Riot Client although both launch RiotClientServices.exe.
/// </summary>
public static partial class EntityMerger
{
    /// <summary>Priority for picking a display name: what Start shows first, the uninstall entry last.</summary>
    private static readonly string[] NamePriority = ["appsfolder", "steam", "epic", "gog", "riot", "shortcut", "uninstall"];

    /// <summary>Priority for the publisher: the uninstall entry is the most reliable.</summary>
    private static readonly string[] PublisherPriority = ["uninstall", "appsfolder", "riot", "steam", "epic", "gog", "shortcut"];

    /// <summary>Folders too generic to mean "one app lives here".</summary>
    private static readonly string[] GenericDirs =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ];

    public sealed record Result(IReadOnlyList<Entity> Entities, IReadOnlyList<Entity> NotLaunchable);

    public static Result Merge(IReadOnlyList<RawApp> raws)
    {
        var generic = GenericDirs.Select(PathUtil.Normalize).OfType<string>().ToHashSet();
        var uf = new UnionFind(raws.Count);
        var owner = new Dictionary<string, int>();

        void Claim(string? key, int i)
        {
            if (key is null) return;
            if (owner.TryGetValue(key, out var j)) uf.Union(i, j);
            else owner[key] = i;
        }

        var dirs = new string?[raws.Count];
        var exes = new string?[raws.Count];
        for (var i = 0; i < raws.Count; i++)
        {
            var r = raws[i];
            exes[i] = PathUtil.Normalize(r.Exe);
            dirs[i] = SpecificDir(r.InstallDir, generic);

            var n = NormalizeName(r.Name);
            // An exe alone is not identity: launchers share one exe across products.
            Claim(exes[i] is { } e ? $"exe:{e}|{n}" : null, i);
            Claim(r.Aumid is { } a ? "aumid:" + a.ToLowerInvariant() : null, i);
            Claim(r.SourceId is { } id && r.Source != "uninstall" ? $"src:{r.Source}:{id}" : null, i);
            Claim(dirs[i] is { } d ? "dir:" + d : null, i);
            Claim(n.Length >= 3 ? "name:" + n : null, i);
        }

        AttachLoneExes(raws, uf, dirs, exes);
        AttachOrphansByExe(raws, uf, exes);

        var groups = Enumerable.Range(0, raws.Count).GroupBy(uf.Find).Select(g => g.Select(i => raws[i]).ToList());
        var entities = new List<Entity>();
        var notLaunchable = new List<Entity>();
        foreach (var group in groups)
        {
            var entity = Build(group);
            (entity.Exe is null && entity.Aumid is null && entity.LaunchUri is null ? notLaunchable : entities).Add(entity);
        }

        return new Result(
            entities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            notLaunchable.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// For each install folder, if the sightings under it (deepest folder wins) are one exe under
    /// one name, join them to the folder's owner. Two exes, or one launcher exe under two product
    /// names, means several apps live there: leave them apart.
    /// </summary>
    private static void AttachLoneExes(IReadOnlyList<RawApp> raws, UnionFind uf, string?[] dirs, string?[] exes)
    {
        var dirOwners = Enumerable.Range(0, raws.Count).Where(i => dirs[i] is not null)
            .GroupBy(i => dirs[i]!).ToDictionary(g => g.Key, g => g.First());

        var exesByDir = new Dictionary<string, List<int>>();
        for (var i = 0; i < raws.Count; i++)
        {
            if (exes[i] is not { } exe) continue;
            var deepest = dirOwners.Keys.Where(d => PathUtil.IsUnder(exe, d)).MaxBy(PathUtil.Depth);
            if (deepest is null) continue;
            if (!exesByDir.TryGetValue(deepest, out var list)) exesByDir[deepest] = list = [];
            list.Add(i);
        }

        foreach (var (dir, members) in exesByDir)
        {
            if (members.Select(i => exes[i]).Distinct().Count() != 1) continue;
            if (members.Select(i => NormalizeName(raws[i].Name)).Distinct().Count() != 1) continue;
            foreach (var i in members) uf.Union(i, dirOwners[dir]);
        }
    }

    /// <summary>Sources that name an app the way the user sees it. Shortcuts and uninstall entries only corroborate.</summary>
    private static readonly HashSet<string> PrimarySources = ["appsfolder", "steam", "epic", "gog", "riot"];

    /// <summary>
    /// A shortcut or uninstall sighting whose group still has no primary sighting joins the one
    /// primary app whose group uses the same exe ("Telegram Desktop" joins "Telegram"). The exe
    /// may come from any sighting in that group: Store-registered desktop apps report an AUMID
    /// to the AppsFolder and their exe only through a shortcut. Skipped when two primary apps
    /// share the exe (Riot Client and League of Legends).
    /// </summary>
    private static void AttachOrphansByExe(IReadOnlyList<RawApp> raws, UnionFind uf, string?[] exes)
    {
        var groupsWithPrimary = Enumerable.Range(0, raws.Count)
            .Where(i => PrimarySources.Contains(raws[i].Source)).Select(uf.Find).ToHashSet();

        var primaryGroupsByExe = new Dictionary<string, HashSet<int>>();
        for (var i = 0; i < raws.Count; i++)
        {
            if (exes[i] is not { } exe || !groupsWithPrimary.Contains(uf.Find(i))) continue;
            if (!primaryGroupsByExe.TryGetValue(exe, out var set)) primaryGroupsByExe[exe] = set = [];
            set.Add(uf.Find(i));
        }

        for (var i = 0; i < raws.Count; i++)
        {
            if (exes[i] is not { } exe || groupsWithPrimary.Contains(uf.Find(i))) continue;
            if (primaryGroupsByExe.TryGetValue(exe, out var targets) && targets.Count == 1) uf.Union(i, targets.First());
        }
    }

    private static string? SpecificDir(string? dir, HashSet<string> generic)
    {
        var d = PathUtil.Normalize(dir);
        if (d is null || generic.Contains(d) || PathUtil.Depth(d) < 1) return null;
        // A drive root like "d:" has depth 0; "d:\games" has depth 1 and is allowed.
        return d;
    }

    private static Entity Build(List<RawApp> group)
    {
        RawApp? First(string[] priority, Func<RawApp, bool> has) =>
            priority.SelectMany(s => group.Where(r => r.Source == s)).FirstOrDefault(has);

        var named = First(NamePriority, _ => true)!;
        var exe = First(NamePriority, r => r.Exe is not null && File.Exists(r.Exe))?.Exe
                  ?? First(NamePriority, r => r.Exe is not null)?.Exe;
        var version = VersionReader.Read(exe);

        var entity = new Entity
        {
            Id = StableId(group),
            Name = named.Name,
            Publisher = First(PublisherPriority, r => !string.IsNullOrWhiteSpace(r.Publisher))?.Publisher ?? version?.CompanyName,
            Exe = exe,
            Args = group.FirstOrDefault(r => r.Args is not null && PathUtil.Normalize(r.Exe) == PathUtil.Normalize(exe))?.Args,
            Aumid = First(NamePriority, r => r.Aumid is not null)?.Aumid,
            InstallDir = First(["steam", "epic", "gog", "riot", "uninstall", "appsfolder"], r => !string.IsNullOrWhiteSpace(r.InstallDir))?.InstallDir,
            LaunchUri = First(NamePriority, r => r.LaunchUri is not null)?.LaunchUri,
            IconPath = group.Select(r => r.IconPath).FirstOrDefault(p => p is not null),
            Version = version,
        };
        entity.Sources.AddRange(group.Select(r => r.Source).Distinct());
        entity.Names.AddRange(group.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var r in group.Where(r => r.SourceId is not null)) entity.SourceIds.TryAdd(r.Source, r.SourceId!);
        return entity;
    }

    /// <summary>Stable across runs: hash of the group's strongest identity.</summary>
    private static string StableId(List<RawApp> group)
    {
        var key = group.Select(r => r.Aumid).FirstOrDefault(a => a is not null)?.ToLowerInvariant()
                  ?? group.Select(r => r.SourceId is null || r.Source == "uninstall" ? null : $"{r.Source}:{r.SourceId}").FirstOrDefault(k => k is not null)
                  ?? group.Select(r => PathUtil.Normalize(r.Exe)).FirstOrDefault(e => e is not null)
                  ?? NormalizeName(group[0].Name);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }

    /// <summary>Per-user installers append " for &lt;username&gt;" (Roblox).</summary>
    private static readonly string ForUserSuffix = " for " + Environment.UserName.ToLowerInvariant();

    /// <summary>"Blender 4.2 (x64)®" and "blender" compare equal, as do "Roblox Player for alice" and "Roblox Player".</summary>
    internal static string NormalizeName(string name)
    {
        var s = name.Trim().ToLowerInvariant();
        if (s.EndsWith(ForUserSuffix, StringComparison.Ordinal)) s = s[..^ForUserSuffix.Length];
        s = Noise().Replace(s, " ");
        s = VersionTail().Replace(s, "");
        return NonAlnum().Replace(s, "");
    }

    [GeneratedRegex(@"[®™©]|\((x64|x86|64-bit|32-bit|64 bit|32 bit)\)|\b(x64|x86|64-bit|32-bit)\b")]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)*\s*$")]
    private static partial Regex VersionTail();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlnum();

    private sealed class UnionFind(int n)
    {
        private readonly int[] _parent = Enumerable.Range(0, n).ToArray();

        public int Find(int x)
        {
            while (_parent[x] != x) x = _parent[x] = _parent[_parent[x]];
            return x;
        }

        public void Union(int a, int b) => _parent[Find(a)] = Find(b);
    }
}
