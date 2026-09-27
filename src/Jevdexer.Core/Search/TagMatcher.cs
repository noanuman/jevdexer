namespace Jevdexer.Core.Search;

/// <summary>
/// Offline query-to-tag mapping by the keywords that ship with the tag list
/// ("video editor" -> video_editing). When online, Jev's own tag choice replaces this map.
/// </summary>
public static class TagMatcher
{
    public static Dictionary<string, double> FromKeywords(string query, TagList tags)
    {
        var q = " " + LocalMatcher.Normalize(query) + " ";
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, double>();
        if (qWords.Length == 0) return result;

        foreach (var (id, tag) in tags.Tags)
        {
            var weight = 0.0;
            foreach (var keyword in tag.Keywords.Select(LocalMatcher.Normalize).Append(LocalMatcher.Normalize(id.Replace('_', ' '))))
            {
                if (keyword.Length == 0) continue;
                var kWords = keyword.Split(' ');
                if (q.Contains(" " + keyword + " ", StringComparison.Ordinal)) weight = Math.Max(weight, 1.0);
                // Every keyword word present, words between allowed: "record my screen" for "record screen".
                // Never a part of a multi-word keyword: "video" alone must not mean "video games".
                else if (kWords.Length > 1 && kWords.All(kw => qWords.Any(w => SameWord(w, kw)))) weight = Math.Max(weight, 0.8);
                else if (kWords.Length == 1 && keyword.Length >= 4 && qWords.Any(w => SameWord(w, keyword))) weight = Math.Max(weight, 0.6);
            }
            if (weight > 0) result[id] = weight;
        }
        return result;
    }

    /// <summary>Equal, or one a prefix of the other when both have 4+ letters ("recording" and "record").</summary>
    private static bool SameWord(string a, string b) =>
        a == b || (a.Length >= 4 && b.Length >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));
}
