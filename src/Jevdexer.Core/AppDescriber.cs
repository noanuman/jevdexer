using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jevdexer.Discovery;

namespace Jevdexer.Core;

/// <summary>
/// The only place that decides what Jev learns about an app: names, publisher, exe description
/// and catalog tags. Never a path.
/// </summary>
public static class AppDescriber
{
    /// <summary>Index-time state sent for labeling.</summary>
    public static AppState State(Entity e) => new(
        Name: e.Name,
        Publisher: e.Publisher,
        OtherNames: e.Names.Where(n => n != e.Name).Take(5).ToList(),
        ExeDescription: e.Version?.FileDescription,
        SteamType: e.Enrichment?.SteamType,
        Genres: e.Enrichment?.Genres.Take(10).ToList() ?? [],
        Tags: e.Enrichment?.Tags.Take(20).ToList() ?? [],
        Description: e.Enrichment?.Description);

    /// <summary>One line per app for query-time search: "CapCut by ByteDance (video-editor, video-editing)".</summary>
    public static string Line(Entity e)
    {
        var tags = e.Enrichment is null ? [] : e.Enrichment.Genres.Concat(e.Enrichment.Tags).Take(8).ToList();
        var line = e.Name;
        if (!string.IsNullOrWhiteSpace(e.Publisher)) line += $" by {e.Publisher}";
        if (e.Enrichment?.Description is { } d) line += $": {d}";
        if (tags.Count > 0) line += $" ({string.Join(", ", tags)})";
        return line.Length <= 300 ? line : line[..300];
    }

    public static string InputHash(Entity e, string tagListVersion)
    {
        var json = JsonSerializer.Serialize(State(e)) + "|" + tagListVersion;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }
}

public sealed record AppState(
    string Name,
    string? Publisher,
    IReadOnlyList<string> OtherNames,
    string? ExeDescription,
    string? SteamType,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    // Omitted when absent, so apps without a catalog description keep their input hash (no relabel).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Description = null);
