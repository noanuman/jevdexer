using System.Text.RegularExpressions;

namespace Jevdexer.Core.Search;

/// <summary>
/// Query layer 1: the query against names and publisher, on every keystroke, in memory.
/// Scores 0..1. At or above <see cref="Confident"/> the query is treated as a name and no
/// network call is made.
/// </summary>
public static partial class LocalMatcher
{
    /// <summary>A prefix of a word in the name, or better.</summary>
    public const double Confident = 0.9;

    public static double Score(string query, IReadOnlyList<string> names, string? publisher)
    {
        var q = Normalize(query);
        if (q.Length == 0) return 0;

        var best = 0.0;
        foreach (var name in names) best = Math.Max(best, ScoreName(q, Normalize(name)));
        if (publisher is not null && Normalize(publisher).StartsWith(q, StringComparison.Ordinal) && q.Length >= 3)
            best = Math.Max(best, 0.5);
        return best;
    }

    private static double ScoreName(string q, string name)
    {
        if (name.Length == 0) return 0;
        if (name == q) return 1.0;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 0.97;

        var words = Words(name);
        var qWords = Words(q);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 0.92;

        // Every query word starts some name word, in any order: "code studio" -> Visual Studio Code.
        if (qWords.Length > 1 && qWords.All(qw => words.Any(w => w.StartsWith(qw, StringComparison.Ordinal)))) return 0.9;

        // Initials: "vsc" -> Visual Studio Code. As deliberate as typing the name, so confident.
        if (q.Length >= 2 && !q.Contains(' ') && string.Concat(words.Select(w => w[0])).StartsWith(q, StringComparison.Ordinal)) return 0.9;

        if (q.Length >= 3 && name.Contains(q, StringComparison.Ordinal)) return 0.8;

        // Typos: one from five letters, two from eight ("photshop", "chorme"). Four letters is too
        // short: "prem" would match "prom"pt.
        if (q.Length >= 5)
        {
            var allowed = q.Length >= 8 ? 2 : 1;
            if (words.Append(name).Any(w => Distance(q, w.Length > q.Length ? w[..q.Length] : w) <= allowed)) return 0.75;
        }
        return 0;
    }

    public static string Normalize(string s) => Spaces().Replace(NonAlnum().Replace(s.ToLowerInvariant(), " "), " ").Trim();

    private static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Optimal string alignment distance (Damerau-Levenshtein with adjacent transpositions).</summary>
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[a.Length, b.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
