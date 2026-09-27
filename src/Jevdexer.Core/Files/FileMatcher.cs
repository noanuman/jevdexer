using Jevdexer.Core.Search;

namespace Jevdexer.Core.Files;

public sealed record FileHit(FileEntry Entry, double Score);

/// <summary>
/// Name search over the file index. Local only: file and folder names never leave the PC.
///
///   1.0   the name is the query
///   0.95  the name starts with it
///   0.9   a word of the name starts with it
///   0.88  every query word starts a word of the name, or is the file's type ("report pdf")
///   0.8   the name contains it
///   0.6   every query word appears somewhere in the path ("tax 2023" -> Work\Tax\2023\...)
///
/// Ties go to the more recently modified, then to folders, then to shallower paths.
/// </summary>
public static class FileMatcher
{
    public static List<FileHit> Search(IReadOnlyList<FileEntry> entries, string query, bool folders, int max)
    {
        var q = LocalMatcher.Normalize(query);
        if (q.Length < 2) return [];
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var hits = new List<FileHit>();
        foreach (var e in entries)
        {
            if (e.IsFolder != folders) continue;
            var score = Score(e, q, qWords);
            if (score > 0) hits.Add(new FileHit(e, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Entry.Modified)
            .ThenBy(h => h.Entry.Path.Length)
            .Take(max)
            .ToList();
    }

    /// <summary>Build output, caches and system files: findable, but below documents, media and programs.</summary>
    private static readonly HashSet<string> LowValue =
    [
        "dll", "pdb", "so", "lib", "obj", "o", "a", "exp", "ilk", "map", "lock", "cache", "bin", "dat", "log",
        "sys", "mui", "cat", "manifest", "nupkg", "pak", "class", "pyc", "tlog", "idx", "etl", "db-wal", "db-shm",
    ];

    internal static double Score(FileEntry e, string q, string[] qWords)
    {
        var score = Match(e, q, qWords);
        return score > 0 && LowValue.Contains(e.Extension) ? score * 0.7 : score;
    }

    private static double Match(FileEntry e, string q, string[] qWords)
    {
        var key = e.Key;
        if (key.Length == 0) return 0;
        if (key == q) return 1.0;
        if (key.StartsWith(q, StringComparison.Ordinal)) return 0.95;

        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 0.9;
        if (qWords.Length > 1 && qWords.All(qw => qw == e.Extension || words.Any(w => w.StartsWith(qw, StringComparison.Ordinal)))
            && qWords.Any(qw => qw != e.Extension))
            return 0.88;
        if (q.Length >= 3 && key.Contains(q, StringComparison.Ordinal)) return 0.8;

        if (qWords.Length > 1)
        {
            var path = LocalMatcher.Normalize(e.Path);
            if (qWords.All(qw => path.Contains(qw, StringComparison.Ordinal))
                && qWords.Any(qw => key.Contains(qw, StringComparison.Ordinal) || words.Any(w => w.StartsWith(qw, StringComparison.Ordinal))))
                return 0.6;
        }
        return 0;
    }
}
