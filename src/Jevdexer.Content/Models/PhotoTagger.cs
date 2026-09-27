using System.Reflection;
using System.Text.Json;
using Jevdexer.Content.Extract;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jevdexer.Content.Models;

/// <summary>A concept a photo shows, with CLIP's relative confidence.</summary>
public sealed record PhotoTag(string Label, float Score, bool Sensitive);

/// <summary>
/// Local photo tags: CLIP ViT-B/32's image encoder (int8, CPU) against a fixed vocabulary of 140
/// concepts embedded ahead of time (tools/ultra/photo_vocab.py, photo-vocab.json). Tags are the
/// concepts whose softmax share (CLIP's logit scale of 100) clears a floor. Also returns the image
/// vector, so later searches can match photos directly.
/// </summary>
public sealed class PhotoTagger : IDisposable
{
    public const int Dimensions = 512;
    private const uint InputSize = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly InferenceSession _session;
    private static readonly Lazy<(string Label, bool Sensitive, float[] Vector)[]> Vocabulary = new(LoadVocabulary);

    public PhotoTagger(byte[] model, int threads = 1)
    {
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(model, options);
    }

    public static IReadOnlyList<string> Labels => Vocabulary.Value.Select(v => v.Label).ToList();

    public async Task<(float[] Vector, List<PhotoTag> Tags)> TagAsync(byte[] image, int max = 5, float floor = 0.08f)
    {
        var bitmap = await Imaging.DecodeSquareAsync(image, InputSize);
        var vector = Embed(Imaging.Pixels(bitmap));
        return (vector, Tags(vector, max, floor));
    }

    public static List<PhotoTag> Tags(float[] imageVector, int max = 5, float floor = 0.08f)
    {
        var vocab = Vocabulary.Value;
        var logits = vocab.Select(v => 100f * Vectors.Dot(imageVector, v.Vector)).ToArray();
        var top = logits.Max();
        var exp = logits.Select(l => MathF.Exp(l - top)).ToArray();
        var sum = exp.Sum();
        return vocab.Select((v, i) => new PhotoTag(v.Label, exp[i] / sum, v.Sensitive))
            .Where(t => t.Score >= floor).OrderByDescending(t => t.Score).Take(max).ToList();
    }

    private float[] Embed(byte[] bgra)
    {
        const int n = (int)InputSize;
        var pixels = new DenseTensor<float>([1, 3, n, n]);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var o = 4 * (y * n + x);
                pixels[0, 0, y, x] = (bgra[o + 2] / 255f - Mean[0]) / Std[0]; // R
                pixels[0, 1, y, x] = (bgra[o + 1] / 255f - Mean[1]) / Std[1]; // G
                pixels[0, 2, y, x] = (bgra[o] / 255f - Mean[2]) / Std[2];     // B
            }
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", pixels)]);
        var v = results.First(r => r.Name == "image_embeds").AsEnumerable<float>().ToArray();
        Vectors.Normalize(v);
        return v;
    }

    private static (string, bool, float[])[] LoadVocabulary()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jevdexer.Content.photo-vocab.json")
                           ?? throw new InvalidOperationException("photo-vocab.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("concepts").EnumerateArray().Select(c =>
        {
            var bytes = Convert.FromBase64String(c.GetProperty("vector").GetString()!);
            var v = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
            return (c.GetProperty("label").GetString()!, c.GetProperty("sensitive").GetBoolean(), v);
        }).ToArray();
    }

    public void Dispose() => _session.Dispose();
}
