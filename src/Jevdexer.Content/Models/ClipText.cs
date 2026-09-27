using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jevdexer.Content.Models;

/// <summary>
/// CLIP ViT-B/32's text encoder (int8), so a search ("a woman in a checked blazer") is compared
/// with each photo's stored CLIP image vector directly, not only through the fixed tag list.
/// English works best (CLIP was trained on English captions). Runs in the app, on the user's own
/// query only; photos are encoded in the sandboxed worker (<see cref="PhotoTagger"/>).
/// </summary>
public sealed partial class ClipText : IDisposable
{
    public const int Dimensions = 512;
    private const int MaxTokens = 77;
    private const int Start = 49406, End = 49407;

    private readonly InferenceSession _session;
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks;
    private readonly Dictionary<string, string[]> _cache = new(StringComparer.Ordinal);
    private static readonly string[] ByteToChar = BytesToUnicode();

    public ClipText(byte[] model, byte[] tokenizerJson)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(model, options);
        using var doc = JsonDocument.Parse(tokenizerJson);
        var m = doc.RootElement.GetProperty("model");
        _vocab = m.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        _ranks = new();
        var rank = 0;
        foreach (var merge in m.GetProperty("merges").EnumerateArray())
        {
            var parts = merge.ValueKind == JsonValueKind.Array
                ? [merge[0].GetString()!, merge[1].GetString()!]
                : merge.GetString()!.Split(' ', 2);
            _ranks.TryAdd((parts[0], parts[1]), rank++);
        }
    }

    /// <summary>Unit vector in CLIP's image-text space.</summary>
    public float[] Embed(string text)
    {
        var ids = Encode(text);
        var input = new DenseTensor<long>([1, ids.Length]);
        for (var i = 0; i < ids.Length; i++) input[0, i] = ids[i];
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", input)]);
        var v = results.First(r => r.Name == "text_embeds").AsEnumerable<float>().ToArray();
        Vectors.Normalize(v);
        return v;
    }

    /// <summary>Token ids with start and end markers, like the reference tokenizer (checked in ModelTests).</summary>
    public int[] Encode(string text)
    {
        var normalized = Spaces().Replace(text.Normalize(NormalizationForm.FormC), " ").ToLowerInvariant().Trim();
        var ids = new List<int> { Start };
        foreach (Match piece in Pieces().Matches(normalized))
        {
            var symbols = string.Concat(Encoding.UTF8.GetBytes(piece.Value).Select(b => ByteToChar[b]));
            foreach (var token in Bpe(symbols))
            {
                ids.Add(_vocab.TryGetValue(token, out var id) ? id : End);
                if (ids.Count >= MaxTokens - 1) break;
            }
            if (ids.Count >= MaxTokens - 1) break;
        }
        ids.Add(End);
        return [.. ids];
    }

    /// <summary>Byte-pair merges on one word, its last symbol carrying the end-of-word mark.</summary>
    private string[] Bpe(string word)
    {
        if (_cache.TryGetValue(word, out var cached)) return cached;
        var symbols = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(word);
        while (e.MoveNext()) symbols.Add((string)e.Current);
        if (symbols.Count == 0) return [];
        symbols[^1] += "</w>";

        while (symbols.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < symbols.Count - 1; i++)
                if (_ranks.TryGetValue((symbols[i], symbols[i + 1]), out var r) && r < bestRank) (best, bestRank) = (i, r);
            if (best < 0) break;
            // Merge every occurrence of the best pair, left to right.
            var (a, b) = (symbols[best], symbols[best + 1]);
            var merged = new List<string>(symbols.Count);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == a && symbols[i + 1] == b)
                {
                    merged.Add(a + b);
                    i++;
                }
                else merged.Add(symbols[i]);
            }
            symbols = merged;
        }
        var result = symbols.ToArray();
        if (_cache.Count < 10_000) _cache[word] = result;
        return result;
    }

    /// <summary>GPT-2's reversible byte to printable-character table.</summary>
    private static string[] BytesToUnicode()
    {
        var bs = Enumerable.Range('!', '~' - '!' + 1).Concat(Enumerable.Range('¡', '¬' - '¡' + 1)).Concat(Enumerable.Range('®', 'ÿ' - '®' + 1)).ToList();
        var cs = bs.ToList();
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (bs.Contains(b)) continue;
            bs.Add(b);
            cs.Add(256 + n++);
        }
        var table = new string[256];
        for (var i = 0; i < bs.Count; i++) table[bs[i]] = ((char)cs[i]).ToString();
        return table;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+")] private static partial Regex Pieces();

    public void Dispose() => _session.Dispose();
}
