namespace Jevdexer.Core.Search;

public sealed record SearchResult(IndexedApp App, double Score, double Local, double Tags, double? Jev);

/// <summary>
/// Combines the three query layers into one score:
///
///   layer 3 below <see cref="JevNoiseFloor"/> counts as 0 (Jev gives unrelated apps 0.2 to 0.4);
///   semantic = 1 - (1 - 0.9 tags)(1 - jev)   either layer alone can carry an app; tags cap at
///                                            0.9 so they never saturate;
///   semantic = 0.8 semantic + 0.2 (jev / best jev)   Jev's relative preference orders apps the
///                                            tags tie on (four messengers at 0.98 to 0.99
///                                            "messaging": Jev's 0.24 for WhatsApp against 0.17
///                                            decides). It reorders, never admits: only apps
///                                            already at <see cref="MinScore"/>, which stay there,
///                                            and only when Jev's best is 0.15 or more;
///   score    = max(local, semantic)          typing a name always wins.
///
/// Apps Jev labeled as background components keep only half their semantic score, so
/// "gameoverlayrenderer" kinds of things sink unless the user types their name.
/// </summary>
public static class Ranker
{
    public const double JevNoiseFloor = 0.4;
    public const double MinScore = 0.3;
    public const int MaxResults = 12;
    private const double TagCap = 0.9;
    private const double RelativeWeight = 0.2;

    public static List<SearchResult> Rank(
        string query,
        IReadOnlyList<IndexedApp> apps,
        IReadOnlyDictionary<string, double> queryTags,
        IReadOnlyDictionary<string, double>? jev)
    {
        var results = new List<SearchResult>();
        var bestJev = jev is { Count: > 0 } ? jev.Values.Max() : 0;
        foreach (var app in apps)
        {
            var e = app.Entity;
            var local = LocalMatcher.Score(query, e.Names.Count > 0 ? e.Names : [e.Name], e.Publisher);
            var tags = TagScore(app.Label, queryTags);
            double? jevRaw = jev is not null && jev.TryGetValue(e.Id, out var p) ? p : null;
            var jevScore = jevRaw is { } j && j >= JevNoiseFloor ? j : 0;

            var semantic = 1 - (1 - TagCap * tags) * (1 - jevScore);
            if (jevRaw is { } raw && bestJev >= 0.15 && semantic >= MinScore)
                semantic = Math.Max(MinScore, (1 - RelativeWeight) * semantic + RelativeWeight * (raw / bestJev));
            if (app.Label is { Background: >= 0.5 }) semantic *= 0.5;

            var score = Math.Max(local, semantic);
            if (score >= MinScore) results.Add(new SearchResult(app, score, local, tags, jevRaw));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Local)
            .ThenBy(r => r.App.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToList();
    }

    /// <summary>Best of P(query means tag) x P(app has tag) over the query's tags.</summary>
    internal static double TagScore(AppLabel? label, IReadOnlyDictionary<string, double> queryTags)
    {
        if (label is null || queryTags.Count == 0) return 0;
        var best = 0.0;
        foreach (var (tag, weight) in queryTags)
            if (label.Tags.TryGetValue(tag, out var p)) best = Math.Max(best, weight * p);
        return best;
    }
}
