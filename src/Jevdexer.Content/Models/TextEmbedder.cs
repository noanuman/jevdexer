using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jevdexer.Content.Models;

/// <summary>
/// multilingual-e5-small (int8, 384 dimensions) on the CPU: one meaning space for 100 languages,
/// so "math integrals" lands near "Matematika 2, integrali". e5 wants a role prefix: "query: "
/// for searches, "passage: " for content. Mean pooling over the attention mask, then L2 norm, so
/// a dot product is the cosine similarity.
/// </summary>
public sealed class TextEmbedder : IDisposable
{
    public const int Dimensions = 384;
    private const int MaxTokens = 128; // ~80 words; measured ~1 ms per token on one core, so a query is ~10 ms and a chunk ~150 ms

    private readonly InferenceSession _session;
    private readonly UnigramTokenizer _tokenizer;
    private readonly bool _wantsTypeIds;

    public TextEmbedder(byte[] model, byte[] tokenizerJson, int threads = 1)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads, // one core: Ultra Search runs in the background
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(model, options);
        _tokenizer = UnigramTokenizer.Load(tokenizerJson);
        _wantsTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public float[] EmbedQuery(string query) => Embed("query: " + query);

    public float[] EmbedPassage(string text) => Embed("passage: " + text);

    private float[] Embed(string text)
    {
        var ids = _tokenizer.Encode(text, MaxTokens);
        var n = ids.Length;
        var inputIds = new DenseTensor<long>([1, n]);
        var mask = new DenseTensor<long>([1, n]);
        for (var i = 0; i < n; i++)
        {
            inputIds[0, i] = ids[i];
            mask[0, i] = 1;
        }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_wantsTypeIds) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([1, n])));

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>(); // [1, n, 384]
        var v = new float[Dimensions];
        for (var t = 0; t < n; t++)
            for (var d = 0; d < Dimensions; d++) v[d] += hidden[0, t, d];
        Vectors.Normalize(v);
        return v;
    }

    public void Dispose() => _session.Dispose();
}
