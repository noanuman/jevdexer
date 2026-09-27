using System.Text.Json;

namespace Jevdexer.Discovery.Enrichment;

/// <summary>
/// Steam genre and store-tag ids to names, from the embedded `steam-tags.json`. `appinfo.vdf`
/// stores only ids. Tags come from Steam's public popular-tags list (429 of them; rarer tag
/// ids are dropped), genres were verified against the store API. Regenerate when tags look sparse.
/// </summary>
internal static class SteamTags
{
    private static readonly Lazy<(Dictionary<int, string> Genres, Dictionary<int, string> Tags)> Table = new(Load);

    public static string? Genre(int id) => Table.Value.Genres.GetValueOrDefault(id);
    public static string? Tag(int id) => Table.Value.Tags.GetValueOrDefault(id);

    private static (Dictionary<int, string>, Dictionary<int, string>) Load()
    {
        using var stream = typeof(SteamTags).Assembly.GetManifestResourceStream("steam-tags.json")
                           ?? throw new InvalidOperationException("steam-tags.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return (Read(doc.RootElement.GetProperty("genres")), Read(doc.RootElement.GetProperty("tags")));
    }

    private static Dictionary<int, string> Read(JsonElement obj) =>
        obj.EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetString()!);
}
