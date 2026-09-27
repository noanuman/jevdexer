using System.Globalization;
using System.Text.RegularExpressions;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Files;

/// <summary>A folder picked for Smart search, and the scrubbed summary Jev will see.</summary>
public sealed record FolderCandidate(FileEntry Folder, string Summary, double LocalScore);

/// <summary>
/// Smart search for folders, the local half. Opt-in: nothing here runs unless the user turned it on.
///
/// 1. <see cref="Shortlist"/>: up to <see cref="MaxCandidates"/> folders that could be meant, by
///    name and path words, file kinds ("documents", "photos") and years, all computed locally.
/// 2. <see cref="Summarize"/>: a short description of each, <see cref="Scrub"/>bed of emails, phone
///    numbers, long numbers, IBANs, URLs and the user's name before it leaves the PC.
/// Nothing is uploaded at index time; only the shortlist of one descriptive search is ever sent.
/// </summary>
public static partial class FolderSummaries
{
    public const int MaxCandidates = 120;
    private const int SampleNames = 6;

    /// <summary>File kinds a query can name, and the extensions that make them.</summary>
    internal static readonly Dictionary<string, string[]> Kinds = new()
    {
        ["documents"] = ["pdf", "doc", "docx", "odt", "rtf", "txt", "md", "pages"],
        ["spreadsheets"] = ["xls", "xlsx", "ods", "csv", "numbers"],
        ["presentations"] = ["ppt", "pptx", "odp", "key"],
        // Camera formats: PNG, SVG and WebP are mostly icons and web assets, screenshots aside.
        ["photos"] = ["jpg", "jpeg", "heic", "heif", "raw", "dng", "cr2", "cr3", "nef", "arw", "tif", "tiff"],
        ["screenshots"] = ["png"],
        ["videos"] = ["mp4", "mov", "mkv", "avi", "wmv", "webm", "m4v"],
        ["music"] = ["mp3", "flac", "wav", "m4a", "aac", "ogg", "wma"],
        ["code"] = ["cs", "ts", "tsx", "js", "py", "java", "cpp", "c", "h", "go", "rs", "kt", "swift", "rb", "php"],
        ["archives"] = ["zip", "rar", "7z", "tar", "gz"],
        ["installers"] = ["exe", "msi", "msix", "appx"],
        ["ebooks"] = ["epub", "mobi", "azw3"],
        ["designs"] = ["psd", "ai", "fig", "sketch", "xd", "indd", "svg"],
    };

    /// <summary>Words people use for a kind, mapped to it.</summary>
    private static readonly Dictionary<string, string> KindWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["document"] = "documents", ["documents"] = "documents", ["docs"] = "documents", ["pdf"] = "documents", ["pdfs"] = "documents",
        ["paper"] = "documents", ["papers"] = "documents", ["letters"] = "documents", ["invoices"] = "documents", ["invoice"] = "documents",
        ["receipts"] = "documents", ["contracts"] = "documents", ["tax"] = "documents", ["taxes"] = "documents", ["notes"] = "documents",
        ["spreadsheet"] = "spreadsheets", ["spreadsheets"] = "spreadsheets", ["excel"] = "spreadsheets", ["budget"] = "spreadsheets",
        ["presentation"] = "presentations", ["presentations"] = "presentations", ["slides"] = "presentations",
        ["photo"] = "photos", ["photos"] = "photos", ["pictures"] = "photos", ["pics"] = "photos", ["images"] = "photos",
        ["screenshot"] = "screenshots", ["screenshots"] = "screenshots",
        ["video"] = "videos", ["videos"] = "videos", ["movies"] = "videos", ["recordings"] = "videos", ["clips"] = "videos",
        ["music"] = "music", ["songs"] = "music", ["audio"] = "music", ["podcasts"] = "music",
        ["code"] = "code", ["project"] = "code", ["projects"] = "code", ["source"] = "code",
        ["archives"] = "archives", ["backups"] = "archives", ["backup"] = "archives",
        ["installers"] = "installers", ["setup"] = "installers",
        ["books"] = "ebooks", ["ebooks"] = "ebooks",
        ["designs"] = "designs", ["design"] = "designs", ["mockups"] = "designs",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "my", "of", "from", "for", "in", "on", "with", "and", "or", "to", "that", "folder", "folders",
        "files", "file", "where", "is", "are", "i", "me", "our", "all", "some", "stuff", "things", "last", "year",
    };

    // ---------------------------------------------------------------- shortlist

    /// <summary>The folders a descriptive search could mean, best first. Local only.</summary>
    public static List<FolderCandidate> Shortlist(IReadOnlyList<FileEntry> entries, string query, int max = MaxCandidates)
    {
        var words = LocalMatcher.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w)).ToList();
        var years = words.Where(w => w.Length == 4 && int.TryParse(w, out var y) && y is >= 1990 and <= 2100).Select(int.Parse).ToList();
        var kinds = words.Select(w => KindWords.GetValueOrDefault(w)).OfType<string>().Distinct().ToList();
        var terms = words.Where(w => w.Length >= 3 && !years.Contains(int.TryParse(w, out var y) ? y : -1)).ToList();
        if (terms.Count == 0 && years.Count == 0 && kinds.Count == 0) return [];

        var byParent = entries.Where(e => !e.IsFolder).GroupBy(e => e.Parent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var scored = new List<(FileEntry Folder, double Score)>();
        foreach (var folder in entries.Where(e => e.IsFolder))
        {
            if (!byParent.TryGetValue(folder.Path, out var files) || files.Count == 0 || MachineNamed(folder.Name)) continue;
            var score = 0.0;

            var pathWords = LocalMatcher.Normalize(folder.Path);
            var fileWords = string.Join(' ', files.Take(200).Select(f => f.Key));
            foreach (var t in terms)
            {
                if (folder.Key.Contains(t, StringComparison.Ordinal)) score += 3;
                else if (pathWords.Contains(t, StringComparison.Ordinal)) score += 2;
                else if (fileWords.Contains(t, StringComparison.Ordinal)) score += 1.5;
            }
            foreach (var k in kinds)
            {
                var share = files.Count(f => Kinds[k].Contains(f.Extension)) / (double)files.Count;
                score += 2 * share;
            }
            foreach (var y in years)
            {
                var share = files.Count(f => f.Modified.Year == y) / (double)files.Count;
                if (share > 0 || pathWords.Contains(y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) score += 1 + share;
            }
            if (score > 0 && InsideAssets(folder.Path)) score *= 0.3; // a project's or app's images are rarely "my photos"
            if (score > 0) scored.Add((folder, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Folder.Modified)
            .Take(max)
            .Select(s => new FolderCandidate(s.Folder, Summarize(s.Folder, byParent[s.Folder.Path]), s.Score))
            .ToList();
    }

    /// <summary>
    /// Shortlists locally, sends the scrubbed summaries, and returns Jev's probability per
    /// folder. Ids are positional ("f0"...): paths never leave the PC. Empty when nothing qualifies.
    /// </summary>
    public static async Task<List<FileHit>> RefineAsync(
        Remote.JevClient backend, IReadOnlyList<FileEntry> entries, string query, CancellationToken ct)
    {
        var candidates = await Task.Run(() => Shortlist(entries, query), ct);
        if (candidates.Count == 0) return [];
        var requests = candidates.Select((c, i) => new Remote.JevClient.FolderRequest($"f{i}", c.Summary)).ToList();
        var response = await backend.SearchFoldersAsync(query, requests, ct);
        return candidates.Select((c, i) => new FileHit(c.Folder, response.Folders.GetValueOrDefault($"f{i}"))).ToList();
    }

    /// <summary>
    /// Folder results after Smart search: local name matches and Jev's judgements combined by
    /// noisy-OR (as for apps); a folder only Jev found needs p ≥ 0.5.
    /// </summary>
    public static List<FileHit> Merge(IReadOnlyList<FileHit> local, IReadOnlyList<FileHit> jev)
    {
        if (jev.Count == 0) return [.. local];
        var byPath = new Dictionary<string, FileHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in local) byPath[h.Entry.Path] = h;
        foreach (var j in jev)
        {
            if (byPath.TryGetValue(j.Entry.Path, out var l)) byPath[j.Entry.Path] = l with { Score = 1 - (1 - l.Score) * (1 - j.Score) };
            else if (j.Score >= 0.5) byPath[j.Entry.Path] = j;
        }
        return [.. byPath.Values.OrderByDescending(h => h.Score)];
    }

    /// <summary>Folder names that make a folder part of a program or website rather than someone's things.</summary>
    private static readonly HashSet<string> AssetSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "assets", "static", "src", "resources", "res", "icons", "sprites", "textures", "fonts", "locales",
        "cache", "appcache", "librarycache", "logs", "dist", "build", "lib", "vendor", "packages", "api", "ui", "steamui",
    };

    private static bool InsideAssets(string path) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).Any(AssetSegments.Contains);

    /// <summary>
    /// Hashes and ids ("a58588d857b8683f", "1172470"): made by programs, never described by people.
    /// Dates people use as names ("2023", "20230612", "2023-06") are not.
    /// </summary>
    private static bool MachineNamed(string name)
    {
        if (name.Length >= 12 && name.All(char.IsAsciiHexDigit) && name.Any(char.IsAsciiLetter)) return true;
        return name.Length >= 5 && name.All(char.IsAsciiDigit)
               && !DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    // ---------------------------------------------------------------- summary

    /// <summary>
    /// "faktura 2023", in Work / Tax: 42 files (40 pdf, 2 xlsx), e.g. faktura_01, faktura_02; modified Jan 2023 to Dec 2023.
    /// Never a full path: the folder's name and at most its two parents. Scrubbed.
    /// </summary>
    public static string Summarize(FileEntry folder, IReadOnlyList<FileEntry> files)
    {
        var parents = ParentNames(folder.Path, 2);
        var types = files.GroupBy(f => f.Extension.Length > 0 ? f.Extension : "no extension")
            .OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Count()} {g.Key}");
        var samples = files.OrderByDescending(f => f.Modified).Take(SampleNames).Select(f => Path.GetFileNameWithoutExtension(f.Name));
        var from = files.Min(f => f.Modified).ToString("MMM yyyy", CultureInfo.InvariantCulture);
        var to = files.Max(f => f.Modified).ToString("MMM yyyy", CultureInfo.InvariantCulture);

        var summary = $"\"{folder.Name}\"" + (parents.Length > 0 ? $", in {parents}" : "")
                      + $": {files.Count} files ({string.Join(", ", types)}), e.g. {string.Join(", ", samples)}"
                      + $"; modified {(from == to ? from : $"{from} to {to}")}";
        summary = Scrub(summary);
        return summary.Length <= 400 ? summary : summary[..397] + "...";
    }

    /// <summary>Up to <paramref name="count"/> parent folder names, nearest last, without the drive or the user profile.</summary>
    private static string ParentNames(string path, int count)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Path.GetDirectoryName(path) ?? "";
        if (parent.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) parent = parent[profile.Length..];
        var parts = parent.Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.EndsWith(':')).ToArray();
        return string.Join(" / ", parts.TakeLast(count));
    }

    // ---------------------------------------------------------------- scrubbing

    /// <summary>
    /// Removes what identifies a person or an account before anything leaves the PC: emails, URLs,
    /// IBANs, phone numbers, runs of 6+ digits (IDs, account and card numbers, JMBG) and the Windows
    /// user name. Personal names in file names are not detected.
    /// </summary>
    public static string Scrub(string text)
    {
        text = Email().Replace(text, "[email]");
        text = SpelledEmail().Replace(text, "[email]");
        text = Url().Replace(text, "[link]");
        text = Iban().Replace(text, "[account]");
        text = HexId().Replace(text, "[id]");
        // Camera and scanner dates (IMG_20230301) say when, not who: kept.
        text = LongNumber().Replace(text, m =>
            m.Length == 8 && DateTime.TryParseExact(m.Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? m.Value : "[number]");
        text = Phone().Replace(text, "[phone]");
        var user = Environment.UserName;
        if (user.Length >= 3) text = Regex.Replace(text, Regex.Escape(user), "[user]", RegexOptions.IgnoreCase);
        return text;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Email();

    /// <summary>Emails spelled out in file names: "name_at_mail_com", "name at mail dot com".</summary>
    [GeneratedRegex(@"[\w.+-]+(?:_at_|\s+at\s+|\(at\)|\[at\])[\w-]+(?:(?:_|\.|\s+dot\s+)[\w-]+)*?(?:_|\.|\s+dot\s+)(?:com|net|org|rs|io|me|de|uk|eu|info|edu|gov|co)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpelledEmail();

    [GeneratedRegex(@"\b(https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b")]
    private static partial Regex Iban();

    /// <summary>UUIDs and long hex hashes (at least one digit, so words like "defaced" survive).</summary>
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b|\b(?=[a-fA-F]*\d)[0-9a-fA-F]{16,}\b")]
    private static partial Regex HexId();

    [GeneratedRegex(@"(?<!\w)\+?\d[\d \-/().]{7,}\d(?!\w)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\d{6,}")]
    private static partial Regex LongNumber();
}
