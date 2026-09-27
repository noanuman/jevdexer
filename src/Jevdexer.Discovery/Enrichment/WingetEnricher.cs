using Microsoft.Data.Sqlite;
using Windows.Management.Deployment;

namespace Jevdexer.Discovery.Enrichment;

/// <summary>
/// Tags from winget's local package index (`Microsoft.Winget.Source` package, `Public\index.db`):
/// about 15,000 packages with community tags such as `video-editor` or `screen-recording`.
/// The index holds no descriptions. Read-only, no network, nothing run through winget itself.
///
/// Match stages, first hit wins: Uninstall subkey = product code; Store family name = pfn;
/// display name = product code (winget stores ARP names there too); normalized name plus an
/// agreeing publisher. A bare name match is never enough: "Notepad" or "Clock" would hit
/// unrelated packages.
/// </summary>
/// <param name="indexPath">An index.db to use instead of the installed one (tests).</param>
public sealed class WingetEnricher(string? indexPath = null) : IEnricher
{
    private const string SourceFamily = "Microsoft.Winget.Source_8wekyb3d8bbwe";

    private static readonly string[] PreRelease = ["beta", "dev", "canary", "nightly", "preview", "insiders", "insider", "ptb", "alpha"];

    public string Name => "winget";

    public int Enrich(IReadOnlyList<Entity> entities)
    {
        var dbPath = indexPath ?? FindIndex();
        if (dbPath is null) return 0;

        var index = WingetIndex.Load(dbPath);
        var enriched = 0;
        foreach (var e in entities)
        {
            var (stage, candidates) = Candidates(e, index);
            if (candidates.Count == 0) continue;

            var best = candidates.MaxBy(c => Score(e, index.Packages[c], index));
            var pkg = index.Packages[best];
            var enrichment = e.Enrichment ??= new EntityEnrichment();
            enrichment.WingetId = pkg.Id;
            enrichment.WingetMatch = stage;
            enrichment.WingetHash = pkg.Hash8;
            enrichment.AddTags(index.TagsOf(best));
            enriched++;
        }
        return enriched;
    }

    private static (string Stage, List<long> Candidates) Candidates(Entity e, WingetIndex index)
    {
        if (e.SourceIds.TryGetValue("uninstall", out var subkey) && index.ByProductCode(subkey) is { Count: > 0 } pc)
            return ("productcode", pc);

        if (e.Aumid is { } aumid && aumid.IndexOf('!') is > 0 and var bang && index.ByPfn(aumid[..bang]) is { Count: > 0 } pfn)
            return ("pfn", pfn);

        foreach (var name in e.Names)
            if (index.ByProductCode(name) is { Count: > 0 } arp) return ("arpname", arp);

        var publisher = EntityMerger.NormalizeName(e.Publisher ?? "");
        if (publisher.Length == 0) return ("", []);
        foreach (var name in e.Names)
        {
            var hits = index.ByName(EntityMerger.NormalizeName(name)).Where(p => index.PublisherAgrees(p, publisher)).ToList();
            if (hits.Count > 0) return ("name+publisher", hits);
        }
        return ("", []);
    }

    /// <summary>Exact name beats partial; a pre-release variant loses unless the app is one ("Chrome" is not "Chrome Beta").</summary>
    private static int Score(Entity e, WingetIndex.Package p, WingetIndex index)
    {
        var score = 0;
        var pkgName = EntityMerger.NormalizeName(p.Name);
        if (e.Names.Any(n => EntityMerger.NormalizeName(n) == pkgName)) score += 4;

        var id = p.Id.ToLowerInvariant();
        var appName = string.Join(' ', e.Names).ToLowerInvariant();
        if (PreRelease.Any(w => id.Contains(w) && !appName.Contains(w))) score -= 3;

        if (index.PublisherAgrees(p.RowId, EntityMerger.NormalizeName(e.Publisher ?? ""))) score += 1;
        return score;
    }

    private static string? FindIndex()
    {
        try
        {
            var pkg = new PackageManager().FindPackagesForUser(string.Empty, SourceFamily).FirstOrDefault();
            var path = pkg is null ? null : Path.Combine(pkg.InstalledLocation.Path, "Public", "index.db");
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The parts of winget's index.db (schema 2.0) we use, loaded into memory (~15k packages).</summary>
internal sealed class WingetIndex
{
    public sealed record Package(long RowId, string Id, string Name, string? Hash8);

    public Dictionary<long, Package> Packages { get; } = [];
    private readonly Dictionary<long, List<string>> _tags = [];
    private readonly Dictionary<string, List<long>> _productCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<long>> _pfns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<long>> _names = [];
    private readonly Dictionary<long, List<string>> _publishers = [];

    public List<long> ByProductCode(string code) => _productCodes.GetValueOrDefault(code.Trim()) ?? [];
    public List<long> ByPfn(string pfn) => _pfns.GetValueOrDefault(pfn) ?? [];
    public List<long> ByName(string normalized) => normalized.Length < 3 ? [] : _names.GetValueOrDefault(normalized) ?? [];
    public IEnumerable<string> TagsOf(long pkg) => _tags.GetValueOrDefault(pkg) ?? [];

    public bool PublisherAgrees(long pkg, string normalizedPublisher) =>
        normalizedPublisher.Length > 0 && (_publishers.GetValueOrDefault(pkg) ?? [])
            .Any(p => p.Length > 0 && (p.Contains(normalizedPublisher) || normalizedPublisher.Contains(p)));

    public static WingetIndex Load(string path)
    {
        var index = new WingetIndex();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();

        Each(db, "select rowid, id, name, hash from packages", r =>
        {
            var hash8 = r.IsDBNull(3) ? null : Convert.ToHexStringLower((byte[])r.GetValue(3))[..8];
            var p = new Package(r.GetInt64(0), r.GetString(1), r.GetString(2), hash8);
            index.Packages[p.RowId] = p;
            Add(index._names, EntityMerger.NormalizeName(p.Name), p.RowId);
        });
        Each(db, "select t.tag, m.package from tags2 t join tags2_map m on m.tag = t.rowid",
            r => Add(index._tags, r.GetInt64(1), r.GetString(0)));
        Each(db, "select productcode, package from productcodes2", r => Add(index._productCodes, r.GetString(0).Trim(), r.GetInt64(1)));
        Each(db, "select pfn, package from pfns2", r => Add(index._pfns, r.GetString(0), r.GetInt64(1)));
        Each(db, "select norm_name, package from norm_names2", r => Add(index._names, EntityMerger.NormalizeName(r.GetString(0)), r.GetInt64(1)));
        Each(db, "select norm_publisher, package from norm_publishers2",
            r => Add(index._publishers, r.GetInt64(1), EntityMerger.NormalizeName(r.GetString(0))));
        return index;
    }

    private static void Each(SqliteConnection db, string sql, Action<SqliteDataReader> row)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) row(r);
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(value)) list.Add(value);
    }
}
