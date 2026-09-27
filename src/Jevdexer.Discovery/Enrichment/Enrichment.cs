namespace Jevdexer.Discovery.Enrichment;

/// <summary>
/// Free, local metadata about an entity from someone else's catalog. Fed to labeling and to
/// query-time matching; never required (an entity with none is labeled from name and publisher).
/// </summary>
public sealed class EntityEnrichment
{
    /// <summary>winget package id, e.g. "ByteDance.CapCut".</summary>
    public string? WingetId { get; set; }

    /// <summary>How the winget match was made: productcode, pfn, arpname, name+publisher.</summary>
    public string? WingetMatch { get; set; }

    /// <summary>
    /// First 8 hex digits of the package's hash in winget's index: the folder of its version list on
    /// winget's CDN (`/cache/packages/&lt;id&gt;/&lt;hash8&gt;/versionData.mszyml`). Changes when the package does.
    /// </summary>
    public string? WingetHash { get; set; }

    /// <summary>The catalog's one-line description ("A video editing tool packed with features ...").</summary>
    public string? Description { get; set; }

    /// <summary>Steam's own classification: Game, Tool, Application, Demo, ...</summary>
    public string? SteamType { get; set; }

    public List<string> Genres { get; } = [];

    /// <summary>Catalog tags (winget tags, Steam store tags), lower case, deduplicated.</summary>
    public List<string> Tags { get; } = [];

    public void AddTags(IEnumerable<string> tags)
    {
        foreach (var t in tags)
        {
            var tag = t.Trim().ToLowerInvariant();
            if (IsUseful(tag) && !Tags.Contains(tag)) Tags.Add(tag);
        }
    }

    /// <summary>
    /// Drops what adds no meaning: translations of tags already present in English
    /// (winget has "浏览器" beside "browser"), and Store product ids ("9nztwsqntd0s").
    /// </summary>
    internal static bool IsUseful(string tag) =>
        tag.Length > 0
        && tag.All(c => c < 0x250 || char.IsWhiteSpace(c)) // Latin scripts only
        && !IsStoreProductId(tag);

    private static bool IsStoreProductId(string tag) =>
        tag.Length == 12 && tag[0] is ('9' or 'x') && tag.All(char.IsAsciiLetterOrDigit) && tag.Skip(1).Any(char.IsAsciiDigit);
}

/// <summary>Adds catalog metadata to entities in place. Local only; must not throw when its catalog is absent.</summary>
public interface IEnricher
{
    string Name { get; }

    /// <summary>Returns how many entities it enriched.</summary>
    int Enrich(IReadOnlyList<Entity> entities);
}
