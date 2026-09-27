using System.Globalization;
using System.Text.RegularExpressions;
using Jevdexer.Content.Models;
using Jevdexer.Core.Files;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Ultra;

/// <summary>A file or folder Ultra Search matched by what is inside it.</summary>
/// <param name="Why">Shown under the result: the matching passage's first words, the matched words, or what the photos show.</param>
/// <param name="Private">Never sent to Jev: its folder is local-only, or it looks sensitive.</param>
public sealed record ContentHit(string Path, bool IsFolder, double Score, string Why, bool Private);

/// <summary>
/// A search in CLIP's space, for matching photos directly: the query's text vector, and that of a
/// neutral "a photo", whose score is subtracted so photo-like images do not win for any query.
/// </summary>
public sealed record ClipQuery(float[] Query, float[] Neutral);

/// <summary>
/// Ultra Search's in-memory index over <see cref="ContentStore"/> records. Local and free:
/// <list type="bullet">
/// <item>Keywords (Light and Full): query words against each file's keywords, title and tags; a
/// shared 6-letter stem counts ("integrals" and "integralni"). Weighted by rarity across the
/// collection (IDF).</item>
/// <item>Meaning (Full): the query's e5 vector against every chunk vector. e5's cosines sit in a
/// narrow band, so a chunk counts by how far it stands above this query's typical chunk (z-score).</item>
/// </list>
/// Every analyzed file answers as itself, so a matching screenshot opens directly. A folder answers
/// too when two or more of its files match (photos are sampled, so "wedding photos" also finds
/// the folder they came from).
/// </summary>
public sealed partial class ContentIndex
{
    private sealed class Item
    {
        public required ContentRecord Record;
        public required Dictionary<string, int> Terms;
        public required List<(string Head, float[]? Vector)> Chunks;
        public required bool Private;
        /// <summary>CLIP image vector (Full mode photos).</summary>
        public float[]? Photo;
        /// <summary>Documents that mention this file by name (their file names), best first.</summary>
        public List<string> UsedIn = [];
        public string Folder => System.IO.Path.GetDirectoryName(Record.Path) ?? "";
        public bool IsImage => Record.Kind == "photo";
        public DateTime When => Record.Result?.TakenAt ?? Record.Modified;
    }

    private readonly List<Item> _items;
    private readonly Dictionary<string, double> _idf;
    private readonly Dictionary<string, List<ContentRecord>> _unsampledByFolder;

    public int Documents { get; }
    public int Photos { get; }
    public int Failed { get; }
    public bool HasVectors { get; }
    public bool HasPhotoVectors { get; }

    /// <summary>Per selected folder: documents and photos read, and files that could not be read.</summary>
    public IReadOnlyDictionary<string, (int Documents, int Photos, int Failed)> RootCounts => _rootCounts;
    private readonly Dictionary<string, (int Documents, int Photos, int Failed)> _rootCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders (selected roots) with a file that looks sensitive, and why.</summary>
    public IReadOnlyDictionary<string, List<string>> SensitiveRoots { get; }

    public static readonly ContentIndex Empty = new([], []);

    public ContentIndex(IReadOnlyList<ContentRecord> records, IReadOnlyList<UltraFolder> folders)
    {
        var localRoots = folders.Where(f => f.LocalOnly).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sensitive = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _items = [];
        _unsampledByFolder = new(StringComparer.OrdinalIgnoreCase);

        foreach (var r in records)
        {
            var c = _rootCounts.GetValueOrDefault(r.Root);
            _rootCounts[r.Root] = r.Kind switch
            {
                "document" => (c.Documents + 1, c.Photos, c.Failed),
                "photo" or "photo-unsampled" => (c.Documents, c.Photos + 1, c.Failed),
                _ => (c.Documents, c.Photos, c.Failed + 1),
            };
            if (r.Kind == "photo-unsampled")
            {
                var dir = System.IO.Path.GetDirectoryName(r.Path) ?? "";
                if (!_unsampledByFolder.TryGetValue(dir, out var list)) _unsampledByFolder[dir] = list = [];
                list.Add(r);
                Photos++;
                continue;
            }
            if (r.Kind == "failed" || r.Result is not { } res)
            {
                Failed++;
                continue;
            }
            if (r.Kind == "document") Documents++;
            else Photos++;

            var signals = res.Sensitive.ToList();
            if (SensitiveName().IsMatch(System.IO.Path.GetFileName(r.Path))) signals.Add("files named like secrets or IDs");
            if (signals.Count > 0)
            {
                if (!sensitive.TryGetValue(r.Root, out var why)) sensitive[r.Root] = why = [];
                foreach (var s in signals) if (!why.Contains(s)) why.Add(s);
            }

            var terms = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in res.Keywords) terms[ScriptFold.Fold(k.Term)] = terms.GetValueOrDefault(ScriptFold.Fold(k.Term)) + k.Count;
            foreach (var w in Words(res.Title ?? "").Concat(Words(System.IO.Path.GetFileNameWithoutExtension(r.Path))))
                terms[w] = terms.GetValueOrDefault(w) + 3; // title and name words weigh like repeated words
            foreach (var t in res.Tags)
                foreach (var w in Words(t.Label)) terms[w] = terms.GetValueOrDefault(w) + 2;
            // Where it lives says what it belongs to: Projects\Client\Reports\charts.
            foreach (var w in FolderPath(r).SelectMany(Words)) terms[w] = terms.GetValueOrDefault(w) + 2;

            _items.Add(new Item
            {
                Record = r,
                Terms = terms,
                Chunks = res.Chunks.Select(c => (c.Head, c.Vector is null ? null : Vectors.Dequantize(c.Vector))).ToList(),
                Private = signals.Count > 0 || localRoots.Contains(r.Root),
                Photo = res.PhotoVector is { } pv ? Vectors.Dequantize(pv) : null,
            });
        }

        LinkReferences();
        HasVectors = _items.Any(i => i.Chunks.Any(c => c.Vector is not null));
        HasPhotoVectors = _items.Any(i => i.Photo is not null);
        SensitiveRoots = sensitive;
        var n = Math.Max(1, _items.Count);
        _idf = _items.SelectMany(i => i.Terms.Keys).GroupBy(t => t)
            .ToDictionary(g => g.Key, g => Math.Log(1 + n / (double)g.Count()), StringComparer.Ordinal);
    }

    public bool IsEmpty => _items.Count == 0;

    /// <summary>The folders between the selected folder and the file, outermost first.</summary>
    private static string[] FolderPath(ContentRecord r)
    {
        var dir = System.IO.Path.GetDirectoryName(r.Path) ?? "";
        var relative = System.IO.Path.GetRelativePath(r.Root, dir);
        return relative == "." || relative.StartsWith("..") ? [] : relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// A document that names a file ("![](charts/revenue.png)") lends it its name, title and top
    /// words, so "chart images from the sales report" reaches the charts the report uses.
    /// Same-named files: the one nearest the document (the longest shared folder path) wins.
    /// </summary>
    private void LinkReferences()
    {
        var byName = _items.GroupBy(i => System.IO.Path.GetFileName(i.Record.Path).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var doc in _items.Where(i => i.Record.Result?.References.Count > 0))
        {
            var docName = System.IO.Path.GetFileName(doc.Record.Path);
            var lend = Words(System.IO.Path.GetFileNameWithoutExtension(docName)).Concat(Words(doc.Record.Result!.Title ?? ""))
                .Concat(doc.Record.Result.Keywords.Take(8).Select(k => ScriptFold.Fold(k.Term))).Distinct().ToList();
            foreach (var name in doc.Record.Result.References)
            {
                if (!byName.TryGetValue(name, out var targets)) continue;
                var target = targets.MaxBy(t => SharedPrefix(t.Folder, doc.Folder))!;
                if (target == doc) continue;
                foreach (var w in lend) target.Terms[w] = target.Terms.GetValueOrDefault(w) + 1;
                if (!target.UsedIn.Contains(docName)) target.UsedIn.Add(docName);
            }
        }
    }

    private static int SharedPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
        return n;
    }

    /// <summary>Words that ask for a kind of file rather than describe one: "chart images", "the notes".</summary>
    private static readonly HashSet<string> ImageWords = new(StringComparer.Ordinal)
    {
        "image", "images", "picture", "pictures", "photo", "photos", "pic", "pics", "screenshot", "screenshots",
        "chart", "charts", "graph", "graphs", "figure", "figures", "diagram", "diagrams", "png", "jpg",
    };

    private static readonly HashSet<string> DocumentWords = new(StringComparer.Ordinal)
    {
        "document", "documents", "doc", "docs", "pdf", "pdfs", "note", "notes", "draft", "drafts", "text", "article", "articles",
    };

    // ---------------------------------------------------------------- search

    public const double ShowAt = 0.35;

    /// <summary>What to show: confident local matches, best first.</summary>
    /// <param name="queryVector">e5 vector of the query (Full mode), or null.</param>
    public List<ContentHit> Search(string query, float[]? queryVector, int max = 12, ClipQuery? photo = null) =>
        ScoreAll(query, queryVector, photo).Select(x => x.Hit).Where(h => h.Score >= ShowAt).OrderByDescending(h => h.Score).Take(max).ToList();

    /// <summary>
    /// What to ask Jev about: the likeliest files and folders even when none is confident. A small
    /// multilingual model ranks "differential equations exam" near a Serbian exam, not always above
    /// an unrelated English note; Jev tells them apart. Ranked by local score plus the cosine's rank.
    /// </summary>
    public List<ContentHit> Candidates(string query, float[]? queryVector, int max = 30, ClipQuery? photo = null) =>
        ScoreAll(query, queryVector, photo).OrderByDescending(x => x.Rank).Take(max).Select(x => x.Hit).ToList();

    private List<(ContentHit Hit, double Rank)> ScoreAll(string query, float[]? queryVector, ClipQuery? photo = null)
    {
        if (_items.Count == 0) return [];
        var words = Words(query).Where(w => !QueryStop.Contains(w)).Distinct().ToList();
        // "chart images from ..." asks for images: images keep their score, other files drop to 70%
        // ("documents", "notes" the other way round). Pure kind words ("images") then leave the
        // keyword match; describing ones ("chart") stay, as tags match them.
        var wantsImages = words.Any(ImageWords.Contains);
        var wantsDocuments = !wantsImages && words.Any(DocumentWords.Contains);
        words = words.Where(w => !(wantsImages && w is "image" or "images" or "picture" or "pictures" or "pic" or "pics") && !(wantsDocuments && w is "document" or "documents" or "doc" or "docs")).ToList();
        // A word no file uses cannot tell files apart: leave it out of the coverage, so "math
        // integrals" over Serbian notes is judged on "integrals" (Jev can still use "math").
        var matchable = words.Where(w => _idf.ContainsKey(w) || _idf.Keys.Any(t => SharesStem(t, w))).ToList();

        var keyword = _items.Select(i => KeywordScore(i, matchable)).ToArray();
        var meaning = queryVector is null ? new double[_items.Count] : MeaningScores(queryVector, out _);
        var bestChunk = queryVector is null ? new int[_items.Count] : BestChunks(queryVector);
        var cosRank = queryVector is null ? new double[_items.Count] : CosinePercentiles(queryVector);
        // Photos: the query against the picture itself (CLIP), beside keywords and tags.
        var looks = photo is null ? new double[_items.Count] : PhotoScores(photo);
        for (var i = 0; i < _items.Count; i++) meaning[i] = Math.Max(meaning[i], looks[i]);

        var files = new List<(ContentHit, double)>();
        var folderParts = new Dictionary<string, List<(double Score, double Rank, Item Item)>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _items.Count; i++)
        {
            var score = 1 - (1 - 0.9 * keyword[i]) * (1 - meaning[i]);
            if ((wantsImages && !_items[i].IsImage) || (wantsDocuments && _items[i].IsImage)) score *= 0.7;
            var rank = score + 0.5 * cosRank[i];
            if (rank < 0.25 && keyword[i] == 0) continue; // any word match stays a candidate for Jev
            var item = _items[i];
            if (!folderParts.TryGetValue(item.Folder, out var parts)) folderParts[item.Folder] = parts = [];
            parts.Add((score, rank, item));
            // Every analyzed file is a result that opens itself (a screenshot, a scan, a PDF).
            files.Add((new ContentHit(item.Record.Path, false, score,
                    looks[i] >= 0.5 && looks[i] >= keyword[i] ? LooksWhy(item) : Why(item, words, bestChunk[i], meaning[i] > keyword[i]), item.Private), rank));
        }

        // A folder is a result too when several of its files match ("wedding photos" is a folder
        // of them); a folder of many matching photos ranks with its best photo, others below.
        var folders = folderParts.Where(kv => kv.Value.Count >= 2).Select(kv =>
        {
            var top = kv.Value.OrderByDescending(p => p.Rank).Take(3).ToList();
            var photos = kv.Value.Count(p => p.Item.Record.Kind == "photo");
            var weight = photos >= 3 ? 1.0 : 0.8;
            // Never above its own best file: a specific description ("a woman in a checked blazer")
            // wants the photo; the folder it sits in is only context.
            var folderScore = Math.Min(top.Average(p => p.Score) * weight, top.Max(p => p.Score) - 0.02);
            var hit = new ContentHit(kv.Key, true, folderScore, FolderWhy(kv.Key, kv.Value.Select(p => p.Item).ToList()), kv.Value.Any(p => p.Item.Private));
            return (hit, top.Average(p => p.Rank) * weight);
        });

        return [.. files, .. folders];
    }

    /// <summary>
    /// How much better each photo fits the query than "a photo" does, in CLIP's space. With 30+
    /// photos, as a z-score against this query's spread; fewer, on CLIP's absolute scale
    /// (0.005 nothing, 0.035 clearly).
    /// </summary>
    private double[] PhotoScores(ClipQuery q)
    {
        var d = new double[_items.Count];
        var present = new List<double>();
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Photo is not { } v) { d[i] = double.NaN; continue; }
            d[i] = Vectors.Dot(q.Query, v) - Vectors.Dot(q.Neutral, v);
            present.Add(d[i]);
        }
        if (present.Count == 0) return new double[_items.Count];
        var mean = present.Average();
        var sd = Math.Sqrt(present.Sum(x => (x - mean) * (x - mean)) / present.Count);
        return d.Select(x => double.IsNaN(x) ? 0
            : present.Count < 30 || sd < 1e-5 ? Math.Clamp((x - 0.005) / 0.03, 0, 1)
            : 1 / (1 + Math.Exp(-1.8 * ((x - mean) / sd - 2.2)))).ToArray();
    }

    private static string LooksWhy(Item item)
    {
        var tags = item.Record.Result?.Tags.Select(t => t.Label).Take(3).ToList() ?? [];
        return tags.Count > 0 ? "Looks like it: " + string.Join(", ", tags) : "Looks like it";
    }

    /// <summary>Each item's best cosine as a rank among all items: 1 for the closest, 0 for the farthest.</summary>
    private double[] CosinePercentiles(float[] q)
    {
        var best = _items.Select(i => i.Chunks.Where(c => c.Vector is not null).Select(c => Vectors.Dot(q, c.Vector)).DefaultIfEmpty(float.MinValue).Max()).ToArray();
        var order = best.Select((b, i) => (b, i)).OrderBy(x => x.b).ToArray();
        var pct = new double[best.Length];
        for (var r = 0; r < order.Length; r++) pct[order[r].i] = order[r].b == float.MinValue ? 0 : r / (double)Math.Max(1, order.Length - 1);
        return pct;
    }

    private double KeywordScore(Item item, List<string> words)
    {
        if (words.Count == 0) return 0;
        double got = 0, total = 0;
        foreach (var w in words)
        {
            var idf = _idf.GetValueOrDefault(w, Math.Log(1 + _items.Count));
            total += idf;
            if (item.Terms.TryGetValue(w, out var tf)) got += idf * Saturate(tf);
            else if (w.Length >= 5 && item.Terms.FirstOrDefault(t => SharesStem(t.Key, w)) is { Key: not null } near) got += 0.7 * idf * Saturate(near.Value);
        }
        return total == 0 ? 0 : got / total;
    }

    /// <summary>Repeats help, with diminishing returns: 1 use 0.75, 2 uses and more 1.</summary>
    private static double Saturate(int tf) => Math.Min(1, 1.5 * tf / (tf + 1.0));

    /// <summary>
    /// A shared start of at least 5 letters covering most of the shorter word: integrals/integralni,
    /// matrix/matrica, rešenje/rešenja. Not "info"/"informatika" (4 letters).
    /// </summary>
    internal static bool SharesStem(string a, string b)
    {
        // Doubled letters count once: English "differential" and Serbian "diferencijalne".
        a = Undouble(a);
        b = Undouble(b);
        var shorter = Math.Min(a.Length, b.Length);
        var common = 0;
        while (common < shorter && a[common] == b[common]) common++;
        return common >= 6 || (common >= 5 && common >= 0.7 * shorter);
    }

    private static string Undouble(string w)
    {
        if (w.Length < 2) return w;
        var sb = new System.Text.StringBuilder(w.Length);
        foreach (var c in w) if (sb.Length == 0 || sb[^1] != c) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Diagnostics (CLI `ultra-test --explain`): per file, best raw cosine, meaning score and keyword score.</summary>
    public List<(string Path, double Cosine, double Meaning, double Keyword)> Explain(string query, float[]? q)
    {
        var words = Words(query).Where(w => !QueryStop.Contains(w)).Distinct().ToList();
        var meaning = q is null ? new double[_items.Count] : MeaningScores(q, out _);
        return _items.Select((i, n) => (i.Record.Path,
                q is null ? 0 : i.Chunks.Where(c => c.Vector is not null).Select(c => (double)Vectors.Dot(q, c.Vector)).DefaultIfEmpty(0).Max(),
                meaning[n], KeywordScore(i, words)))
            .OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>Per item: how far its best chunk stands above this query's usual chunk, squashed to 0..1.</summary>
    private double[] MeaningScores(float[] q, out double top)
    {
        var best = _items.Select(i => i.Chunks.Where(c => c.Vector is not null).Select(c => (double)Vectors.Dot(q, c.Vector)).DefaultIfEmpty(double.NaN).Max()).ToArray();
        var present = best.Where(b => !double.IsNaN(b)).ToArray();
        top = present.Length == 0 ? 0 : present.Max();
        if (present.Length == 0) return new double[_items.Count];

        var mean = present.Average();
        var sd = Math.Sqrt(present.Sum(b => (b - mean) * (b - mean)) / present.Length);
        return best.Select(b =>
        {
            if (double.IsNaN(b)) return 0.0;
            // Few items: statistics are noise, so use e5's absolute scale (0.80 is unrelated, 0.88 on topic).
            if (present.Length < 30 || sd < 1e-4) return Math.Clamp((b - 0.80) / 0.08, 0, 1);
            var z = (b - mean) / sd;
            return 1 / (1 + Math.Exp(-1.8 * (z - 2.2)));
        }).ToArray();
    }

    private int[] BestChunks(float[] q) => _items.Select(i =>
    {
        var (best, at) = (float.MinValue, 0);
        for (var c = 0; c < i.Chunks.Count; c++)
            if (i.Chunks[c].Vector is { } v && Vectors.Dot(q, v) is var d && d > best) (best, at) = (d, c);
        return at;
    }).ToArray();

    private static string Why(Item item, List<string> words, int chunk, bool byMeaning)
    {
        if (item.IsImage && item.UsedIn.Count > 0) return "Used in " + string.Join(", ", item.UsedIn.Take(2));
        var matched = words.Where(w => item.Terms.ContainsKey(w) || item.Terms.Keys.Any(t => SharesStem(t, w))).ToList();
        if (!byMeaning && matched.Count > 0)
        {
            var hits = item.Terms.Keys.Where(t => words.Any(w => t == w || SharesStem(t, w))).Take(4);
            return "Inside: " + string.Join(", ", hits);
        }
        if (item.Chunks.Count > chunk && item.Chunks[chunk].Head is { Length: > 0 } head)
            return "“" + (head.Length > 90 ? head[..90].TrimEnd() + "…" : head) + "”";
        return "Inside: " + string.Join(", ", item.Terms.OrderByDescending(t => t.Value).Take(4).Select(t => t.Key));
    }

    private string FolderWhy(string folder, List<Item> items)
    {
        var photos = items.Count(i => i.Record.Kind == "photo") + (_unsampledByFolder.GetValueOrDefault(folder)?.Count ?? 0);
        var tags = items.SelectMany(i => i.Record.Result?.Tags ?? []).GroupBy(t => t.Label)
            .OrderByDescending(g => g.Sum(t => t.Score)).Take(3).Select(g => g.Key).ToList();
        var dates = items.Select(i => i.When).Concat(_unsampledByFolder.GetValueOrDefault(folder)?.Select(r => r.Modified) ?? []).ToList();
        var range = dates.Count == 0 ? "" : $" · {Month(dates.Min())}" + (Month(dates.Min()) == Month(dates.Max()) ? "" : $" to {Month(dates.Max())}");
        if (photos > 0 && tags.Count > 0) return $"{photos} photos: {string.Join(", ", tags)}{range}";
        var docs = items.Count(i => i.Record.Kind == "document");
        var words = items.SelectMany(i => i.Terms).GroupBy(t => t.Key).OrderByDescending(g => g.Sum(t => t.Value) * _idf.GetValueOrDefault(g.Key, 1))
            .Take(4).Select(g => g.Key);
        return $"{docs} documents about {string.Join(", ", words)}{range}";
    }

    private static string Month(DateTime d) => d.ToString("MMM yyyy", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- summaries for Jev

    /// <summary>
    /// A scrubbed description of a hit for Jev: its name, kind, dates, its most distinctive words and
    /// what its pictures show. Never passages of text. Null for private hits.
    /// </summary>
    public string? Summary(ContentHit hit)
    {
        if (hit.Private) return null;
        string text;
        if (hit.IsFolder)
        {
            var items = _items.Where(i => string.Equals(i.Folder, hit.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Any(i => i.Private)) return null;
            text = $"folder \"{System.IO.Path.GetFileName(hit.Path)}\": {FolderWhy(hit.Path, items)}";
        }
        else
        {
            var item = _items.FirstOrDefault(i => string.Equals(i.Record.Path, hit.Path, StringComparison.OrdinalIgnoreCase));
            if (item is null || item.Private) return null;
            var res = item.Record.Result!;
            var words = item.Terms.OrderByDescending(t => Saturate(t.Value) * _idf.GetValueOrDefault(t.Key, 1)).Take(12).Select(t => t.Key);
            var shows = res.Tags.Count > 0 ? $"; pictures show {string.Join(", ", res.Tags.Select(t => t.Label))}" : "";
            var pages = res.Pages > 0 ? $", {res.Pages} pages" : "";
            // Relations, as names only: the folders it sits in, and the documents that use it.
            var place = FolderPath(item.Record).TakeLast(3).ToList();
            var where = place.Count > 0 ? $", in {string.Join(" / ", place)}" : "";
            var usedIn = item.UsedIn.Count > 0 ? $"; used in {string.Join(", ", item.UsedIn.Take(3))}" : "";
            text = $"{(item.IsImage ? "image" : "file")} \"{System.IO.Path.GetFileName(hit.Path)}\"{where}{pages}, {Month(item.When)}: about {string.Join(", ", words)}{shows}{usedIn}";
        }
        var scrubbed = FolderSummaries.Scrub(text);
        return scrubbed.Length <= 400 ? scrubbed : scrubbed[..397] + "...";
    }

    /// <summary>
    /// Jev's view merged with the local one, over the candidates Jev saw: noisy-OR with the local
    /// score; a candidate Jev rejects (p &lt; 0.2) drops by half. Only what clears
    /// <see cref="ShowAt"/> is shown, so a weak local candidate appears only if Jev vouches for it.
    /// </summary>
    public static List<ContentHit> Merge(IReadOnlyList<ContentHit> candidates, IReadOnlyDictionary<string, double> jev, int max = 12) =>
        candidates.Select(h => jev.TryGetValue(h.Path, out var p)
                ? h with { Score = p < 0.2 ? h.Score * 0.5 : 1 - (1 - h.Score) * (1 - p) }
                : h)
            .Where(h => h.Score >= ShowAt)
            .OrderByDescending(h => h.Score).Take(max).ToList();

    // ---------------------------------------------------------------- words

    /// <summary>Lower-case words of 3+ letters, in one script (<see cref="ScriptFold"/>).</summary>
    internal static IEnumerable<string> Words(string text) =>
        ScriptFold.Fold(LocalMatcher.Normalize(text)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !w.All(char.IsDigit));

    private static readonly HashSet<string> QueryStop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "from", "with", "about", "my", "files", "file", "folder", "folders", "documents", "document", "stuff", "things", "where", "that",
    };

    /// <summary>File names that announce secrets or identity papers: such files never reach the cloud.</summary>
    [GeneratedRegex(@"(recovery|backup[ _-]?codes?|passwords?|lozink|private[ _-]?key|seed|wallet|passport|paso[sš]|licna|lična|jmbg|id[ _-]?card|medical|zdravstv|nalaz)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();
}
