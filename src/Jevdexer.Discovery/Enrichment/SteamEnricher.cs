using Microsoft.Win32;

namespace Jevdexer.Discovery.Enrichment;

/// <summary>
/// Steam's own view of each installed Steam app, read from the local `appinfo.vdf` cache:
/// type (Game, Tool, Application), genres and store tags. No network.
/// </summary>
public sealed class SteamEnricher : IEnricher
{
    public string Name => "steam";

    public int Enrich(IReadOnlyList<Entity> entities)
    {
        var byAppId = new Dictionary<uint, Entity>();
        foreach (var e in entities)
            if (e.SourceIds.TryGetValue("steam", out var id) && uint.TryParse(id, out var appId)) byAppId[appId] = e;
        if (byAppId.Count == 0) return 0;

        var path = FindAppInfo();
        if (path is null) return 0;

        var info = SteamAppInfo.Read(File.ReadAllBytes(path), byAppId.Keys.ToHashSet());
        foreach (var (appId, common) in info)
        {
            var enrichment = byAppId[appId].Enrichment ??= new EntityEnrichment();
            enrichment.SteamType = common.Type;
            foreach (var g in common.Genres.Select(SteamTags.Genre).OfType<string>())
                if (!enrichment.Genres.Contains(g)) enrichment.Genres.Add(g);
            enrichment.AddTags(common.StoreTags.Select(SteamTags.Tag).OfType<string>());
        }
        return info.Count;
    }

    private static string? FindAppInfo()
    {
        var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (root is null) return null;
        var path = Path.Combine(root.Replace('/', '\\'), "appcache", "appinfo.vdf");
        return File.Exists(path) ? path : null;
    }
}
