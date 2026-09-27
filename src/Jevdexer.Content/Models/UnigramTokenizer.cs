using System.Text;
using System.Text.Json;

namespace Jevdexer.Content.Models;

/// <summary>
/// SentencePiece Unigram tokenizer, read from a Hugging Face tokenizer.json (XLM-RoBERTa family:
/// multilingual-e5). NFKC in place of the "precompiled charsmap" normalizer (the same for
/// ordinary text), Metaspace pre-tokenization (every word starts with "▁"), Viterbi over the
/// vocabulary scores, then &lt;s&gt; ... &lt;/s&gt;. Checked against the reference implementation in
/// UltraTests (ids for Serbian, Cyrillic and English sentences).
/// </summary>
public sealed class UnigramTokenizer
{
    private const char Space = '▁';
    private readonly Dictionary<string, (int Id, float Score)> _vocab;
    private readonly int _maxPieceLength;
    private readonly float _unkScore;

    public int BosId { get; } = 0;
    public int EosId { get; } = 2;
    public int PadId { get; } = 1;
    public int UnkId { get; }

    private UnigramTokenizer(Dictionary<string, (int, float)> vocab, int unkId, int maxPieceLength, float minScore)
    {
        _vocab = vocab;
        UnkId = unkId;
        _maxPieceLength = maxPieceLength;
        _unkScore = minScore - 10f; // SentencePiece's penalty for an unknown character
    }

    public static UnigramTokenizer Load(byte[] tokenizerJson)
    {
        using var doc = JsonDocument.Parse(tokenizerJson);
        var model = doc.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram") throw new InvalidDataException("not a Unigram tokenizer");
        var unkId = model.GetProperty("unk_id").GetInt32();

        var vocab = new Dictionary<string, (int, float)>(260_000, StringComparer.Ordinal);
        var id = 0;
        var maxLength = 1;
        var minScore = 0f;
        foreach (var entry in model.GetProperty("vocab").EnumerateArray())
        {
            var piece = entry[0].GetString()!;
            var score = (float)entry[1].GetDouble();
            vocab.TryAdd(piece, (id, score));
            maxLength = Math.Max(maxLength, piece.Length);
            if (id > 3) minScore = Math.Min(minScore, score);
            id++;
        }
        return new UnigramTokenizer(vocab, unkId, maxLength, minScore);
    }

    /// <summary>Token ids with &lt;s&gt; and &lt;/s&gt;, at most <paramref name="maxTokens"/> long (the end is cut).</summary>
    public int[] Encode(string text, int maxTokens = 512)
    {
        var ids = new List<int>(64) { BosId };
        foreach (var word in Words(Normalize(text)))
        {
            Viterbi(word, ids);
            if (ids.Count >= maxTokens - 1) break;
        }
        if (ids.Count > maxTokens - 1) ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        ids.Add(EosId);
        return [.. ids];
    }

    internal static string Normalize(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        var lastSpace = true; // trims the start
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> Words(string normalized)
    {
        foreach (var w in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)) yield return Space + w;
    }

    /// <summary>Best segmentation of one word by total piece score; unknown characters become &lt;unk&gt; (merged).</summary>
    private void Viterbi(string word, List<int> ids)
    {
        var n = word.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;

        for (var i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i])) continue;
            var matched = false;
            for (var len = 1; len <= _maxPieceLength && i + len <= n; len++)
            {
                if (!_vocab.TryGetValue(word.Substring(i, len), out var v)) continue;
                matched |= len == CharLength(word, i);
                var score = best[i] + v.Score;
                if (score > best[i + len])
                {
                    best[i + len] = score;
                    from[i + len] = i;
                    piece[i + len] = v.Id;
                }
            }
            if (!matched)
            {
                var len = CharLength(word, i);
                var score = best[i] + _unkScore;
                if (score > best[i + len])
                {
                    best[i + len] = score;
                    from[i + len] = i;
                    piece[i + len] = UnkId;
                }
            }
        }

        var path = new List<int>();
        for (var at = n; at > 0; at = from[at]) path.Add(piece[at]);
        path.Reverse();
        foreach (var id in path)
        {
            if (id == UnkId && ids.Count > 0 && ids[^1] == UnkId) continue; // consecutive unknowns fuse
            ids.Add(id);
        }
    }

    private static int CharLength(string s, int i) => char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
}
