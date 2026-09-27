using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Jevdexer.Content.Extract;
using Jevdexer.Content.Models;
using Jevdexer.Content.Worker;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Jevdexer.Content.Analysis;

/// <summary>
/// Turns one file's bytes into a <see cref="ContentResult"/>, inside the worker. Light mode (no
/// models): text, keywords, sensitive signals, OCR. Full mode adds meaning vectors for up to
/// <see cref="MaxChunks"/> chunks of a document, and CLIP tags and a vector for a photo.
/// </summary>
public sealed class Analyzer(TextEmbedder? text, PhotoTagger? photos) : IDisposable
{
    public const int MaxChunks = 4;
    private const int ChunkWords = 80; // about 128 tokens, ~150 ms on one core
    private const int HeadChars = 160;
    private const float SensitivePhotoFloor = 0.45f; // CLIP confuses text screenshots with documents below this

    /// <summary>Labels that mean a photo is mostly text: worth OCR.</summary>
    private static readonly HashSet<string> TextLike =
    [
        "screenshot", "phone screenshot", "chat screenshot", "website", "code", "chart", "diagram", "slide", "spreadsheet", "document",
        "handwritten notes", "whiteboard", "book page", "math", "receipt", "invoice", "ticket", "menu", "poster",
        "id card", "passport", "driving licence", "bank card", "medical document", "prescription", "contract", "bank statement", "password",
    ];

    public async Task<ContentResult> DocumentAsync(byte[] bytes, string extension, string? name = null)
    {
        var extracted = await DocumentText.ReadAsync(bytes, extension);

        // Pages only a picture can describe (handwritten notes, drawings): what they show.
        var tags = new Dictionary<string, PhotoTag>();
        if (photos is not null)
        {
            foreach (var page in extracted.PageImages)
                foreach (var t in (await photos.TagAsync(page)).Tags.Where(t => !t.Sensitive))
                    if (!tags.TryGetValue(t.Label, out var seen) || seen.Score < t.Score) tags[t.Label] = t;
        }

        var result = FromText(extracted.Text, "document");
        var tagList = tags.Values.OrderByDescending(t => t.Score).Take(5).ToList();
        return result with
        {
            Title = extracted.Title,
            Pages = extracted.Pages,
            UsedOcr = extracted.UsedOcr,
            Tags = tagList,
            Chunks = [.. NameChunk(name, extracted.Title), .. result.Chunks, .. TagChunk(tagList, "pages showing")],
        };
    }

    /// <summary>
    /// The file's name and title as their own chunk: people name documents by topic ("Linearna
    /// algebra, rang matrice"), and exam or exercise text rarely states its topic outright.
    /// </summary>
    private IEnumerable<Chunk> NameChunk(string? name, string? title)
    {
        var words = string.Join(". ", new[] { name, title }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (text is null || words.Length < 4) yield break;
        yield return new Chunk(words, Vectors.Quantize(text.EmbedPassage(words)));
    }

    /// <summary>
    /// What a picture shows, as a text chunk in the same meaning space as documents ("a photo
    /// showing wedding, church, cake"), so one query model searches both.
    /// </summary>
    private IEnumerable<Chunk> TagChunk(List<PhotoTag> tags, string lead)
    {
        if (text is null || tags.Count == 0) yield break;
        var labels = string.Join(", ", tags.Select(t => t.Label));
        yield return new Chunk($"{lead}: {labels}", Vectors.Quantize(text.EmbedPassage($"{lead} {labels}")));
    }

    public async Task<ContentResult> PhotoAsync(byte[] bytes, string extension)
    {
        List<PhotoTag> tags = [];
        byte[]? vector = null;
        if (photos is not null)
        {
            var (v, t) = await photos.TagAsync(bytes);
            (vector, tags) = (Vectors.Quantize(v), t);
        }

        // Screens and papers carry their meaning as text. Without the photo model, PNGs are
        // mostly screenshots: read those too.
        var readText = tags.Any(t => TextLike.Contains(t.Label)) || (photos is null && extension.Equals("png", StringComparison.OrdinalIgnoreCase));
        var ocr = readText ? await Ocr.ReadAsync(bytes) : "";
        var result = FromText(ocr, "photo");

        var sensitive = result.Sensitive.ToList();
        sensitive.AddRange(tags.Where(t => t.Sensitive && t.Score >= SensitivePhotoFloor).Select(t => "photo of " + t.Label));
        var kept = tags.Where(t => !t.Sensitive || t.Score >= SensitivePhotoFloor).ToList();
        return result with
        {
            UsedOcr = ocr.Length > 0,
            Tags = kept,
            Chunks = [.. result.Chunks, .. TagChunk(kept, "a photo showing")],
            PhotoVector = vector,
            TakenAt = await TakenAtAsync(bytes),
            Sensitive = sensitive,
        };
    }

    private ContentResult FromText(string content, string kind) => new()
    {
        Kind = kind,
        TextLength = content.Length,
        Keywords = Keywords.Top(content),
        Chunks = Chunks(content),
        Sensitive = SensitiveSignals.Find(content),
        References = References.Find(content),
    };

    /// <summary>Up to four windows of 80 words spread over the text, the first always included.</summary>
    private List<Chunk> Chunks(string content)
    {
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var windows = Math.Max(1, (int)Math.Ceiling(words.Length / (double)ChunkWords));
        var picks = Enumerable.Range(0, Math.Min(MaxChunks, windows))
            .Select(i => (int)Math.Round(i * (windows - 1) / (double)Math.Max(1, Math.Min(MaxChunks, windows) - 1)))
            .Distinct();
        var chunks = new List<Chunk>();
        foreach (var w in picks)
        {
            var passage = string.Join(' ', words.Skip(w * ChunkWords).Take(ChunkWords));
            var head = passage.Length <= HeadChars ? passage : passage[..HeadChars];
            chunks.Add(new Chunk(head, text is null ? null : Vectors.Quantize(text.EmbedPassage(passage))));
        }
        return chunks;
    }

    /// <summary>EXIF DateTimeOriginal: when the photo was taken, even if the file was copied since.</summary>
    private static async Task<DateTime?> TakenAtAsync(byte[] bytes)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var props = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.DateTaken"]);
            return props.TryGetValue("System.Photo.DateTaken", out var v) && v.Value is DateTimeOffset d ? d.UtcDateTime : null;
        }
        catch (Exception)
        {
            return null; // no EXIF, or a format without it
        }
    }

    /// <summary>Times the models on built-in samples, one core. Nothing of the user's is read.</summary>
    public async Task<BenchmarkResult> BenchmarkAsync()
    {
        double passageMs = 0, photoMs = 0;
        if (text is not null)
        {
            var sample = string.Join(' ', Enumerable.Repeat("Određeni integral, Njutn-Lajbnicova formula i smena promenljivih u zadacima sa ispita.", 14));
            text.EmbedPassage(sample); // warm-up
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) text.EmbedPassage(sample);
            passageMs = sw.Elapsed.TotalMilliseconds / 5;
        }
        if (photos is not null)
        {
            var image = await SampleImageAsync();
            await photos.TagAsync(image);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 3; i++) await photos.TagAsync(image);
            photoMs = sw.Elapsed.TotalMilliseconds / 3;
        }
        return new BenchmarkResult(Math.Round(passageMs, 1), Math.Round(photoMs, 1), text is not null, photos is not null);
    }

    /// <summary>A 1600 x 1200 gradient, JPEG-encoded: the size of a phone photo after decoding work.</summary>
    private static async Task<byte[]> SampleImageAsync()
    {
        const int w = 1600, h = 1200;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = 4 * (y * w + x);
                pixels[o] = (byte)(x * 255 / w);
                pixels[o + 1] = (byte)(y * 255 / h);
                pixels[o + 2] = (byte)((x + y) % 256);
                pixels[o + 3] = 255;
            }
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    public void Dispose()
    {
        text?.Dispose();
        photos?.Dispose();
    }
}
