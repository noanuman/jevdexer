using Jevdexer.Content.Models;

namespace Jevdexer.Core.Tests;

/// <summary>
/// Checks against the downloaded models (%LOCALAPPDATA%\Jevdexer\models). Machines without them
/// (CI) pass these trivially: the models are a 225 MB opt-in download, not part of the repo.
/// </summary>
public class ModelTests
{
    internal static readonly string ModelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jevdexer", "models");
    internal static byte[]? Read(string name) => File.Exists(Path.Combine(ModelDir, name)) ? File.ReadAllBytes(Path.Combine(ModelDir, name)) : null;

    private static readonly Lazy<UnigramTokenizer?> Tokenizer = new(() => Read("e5-tokenizer.json") is { } b ? UnigramTokenizer.Load(b) : null);

    // Expected ids from the reference implementation (Python `tokenizers` 0.22, same tokenizer.json).
    [Theory]
    [InlineData("query: math integrals", new[] { 0, 41, 1294, 12, 48909, 28970, 7, 2 })]
    [InlineData("passage: SI Matematika 2 grupe P2 i P3 - predavanja 07", new[] { 0, 46692, 12, 10000, 167657, 116, 52760, 436, 304, 17, 436, 363, 20, 82616, 11, 4719, 2 })]
    [InlineData("Линеарна зависност вектора, ранг матрица", new[] { 0, 9762, 336, 53218, 157258, 21714, 33891, 4, 119967, 37765, 34552, 2 })]
    [InlineData("  Hello,   World! ﬁle №5 café", new[] { 0, 35378, 4, 6661, 38, 11435, 438, 758, 26216, 2 })]
    [InlineData("passage: Određeni integral i Njutn-Lajbnicova formula", new[] { 0, 46692, 12, 2365, 106466, 14, 28970, 17, 541, 33009, 19, 9, 2729, 170, 275, 12150, 330, 26168, 2 })]
    public void Tokenizer_matches_the_reference(string text, int[] expected)
    {
        if (Tokenizer.Value is not { } t) return;
        Assert.Equal(expected, t.Encode(text));
    }

    private static readonly Lazy<ClipText?> Clip = new(() =>
        Read("clip-text-int8.onnx") is { } m && Read("clip-tokenizer.json") is { } t ? new ClipText(m, t) : null);

    // Expected ids from the reference implementation (Python `tokenizers`, Xenova/clip-vit-base-patch32 tokenizer.json).
    [Theory]
    [InlineData("Image of a woman", new[] { 49406, 2867, 539, 320, 2308, 49407 })]
    [InlineData("a woman in a checked blazer!", new[] { 49406, 320, 2308, 530, 320, 10387, 17606, 256, 49407 })]
    [InlineData("Café 2023, don't STOP", new[] { 49406, 15304, 273, 271, 273, 274, 267, 847, 713, 1691, 49407 })]
    [InlineData(" multiple   spaces ", new[] { 49406, 6470, 9006, 49407 })]
    public void Clip_tokenizer_matches_the_reference(string text, int[] expected)
    {
        if (Clip.Value is not { } clip) return;
        Assert.Equal(expected, clip.Encode(text));
    }

    [Fact]
    public void Embeddings_bridge_languages()
    {
        if (Read("e5-small-int8.onnx") is not { } model || Read("e5-tokenizer.json") is not { } tok) return;
        using var e = new TextEmbedder(model, tok);
        var q = e.EmbedQuery("math integrals");
        var math = Vectors.Dot(q, e.EmbedPassage("Matematika 2, predavanja: određeni integral, Njutn-Lajbnicova formula, smena promenljivih"));
        var cooking = Vectors.Dot(q, e.EmbedPassage("Recept za sarmu: kiseli kupus, mleveno meso, pirinač"));
        var cyrillic = Vectors.Dot(q, e.EmbedPassage("Интеграли: неодређени интеграл, парцијална интеграција"));
        // Reference: Python onnxruntime on the same file gives 0.8594, 0.7750, 0.8126. e5 packs
        // similarities into 0.7-0.9, so ranking uses the gap above the best unrelated score, not a fixed bar.
        Assert.Equal(0.8594, math, 0.002);
        Assert.Equal(0.7750, cooking, 0.002);
        Assert.Equal(0.8126, cyrillic, 0.002);
    }
}
