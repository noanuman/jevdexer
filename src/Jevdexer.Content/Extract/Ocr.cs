using Windows.Globalization;
using Windows.Media.Ocr;

namespace Jevdexer.Content.Extract;

/// <summary>
/// Text in images with Windows' built-in OCR (Windows.Media.Ocr): no model to download, CPU only,
/// in the languages Windows has OCR packs for. The user's first language goes first; when its
/// output looks like noise (wrong script, or handwriting), the other installed scripts are tried
/// (Serbian has separate Cyrillic and Latin engines). Output that still looks like noise is dropped:
/// no text is better than wrong keywords. Handwriting is generally not readable.
/// </summary>
public static class Ocr
{
    private const double GoodEnough = 0.6;
    private const double Keep = 0.45;

    private static readonly Lazy<List<OcrEngine>> Engines = new(() =>
    {
        var engines = new List<OcrEngine>();
        if (OcrEngine.TryCreateFromUserProfileLanguages() is { } first) engines.Add(first);
        // Then one Cyrillic engine and one Latin engine with South Slavic letters (č ć š ž đ), when
        // installed and not already first. At most three: each is a full pass over the image.
        foreach (var group in new[] { new[] { "sr-Cyrl-RS", "ru-RU", "uk-UA", "bg-BG" }, ["sr-Latn-RS", "hr-HR", "bs-Latn-BA", "en-US"] })
        {
            if (engines.Count >= 3) break;
            if (engines.Any(e => group.Contains(e.RecognizerLanguage.LanguageTag))) continue;
            foreach (var tag in group)
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang) && OcrEngine.TryCreateFromLanguage(lang) is { } engine)
                {
                    engines.Add(engine);
                    break;
                }
            }
        }
        return engines;
    });

    private const string Vowels = "aeiouyáéíóúàèìòùâêîôûäëïöüаеиоуэюяєії";

    public static bool Available => Engines.Value.Count > 0;

    /// <summary>Recognized text, or empty when nothing readable was found.</summary>
    public static async Task<string> ReadAsync(byte[] image)
    {
        if (!Available) return "";
        var bitmap = await Imaging.DecodeAsync(image, OcrEngine.MaxImageDimension);
        var (best, bestQuality) = ("", 0.0);
        foreach (var engine in Engines.Value)
        {
            var result = await engine.RecognizeAsync(bitmap);
            var text = string.Join('\n', result.Lines.Select(l => l.Text));
            var quality = Quality(text);
            if (quality > bestQuality) (best, bestQuality) = (text, quality);
            if (quality >= GoodEnough) break;
        }
        return bestQuality >= Keep ? best : "";
    }

    /// <summary>
    /// Share of tokens that look like words: letters only, 2+ long, with a vowel. OCR noise
    /// ("юњ-ети..2.„ььа", "(eroô4") scores low; real text, even short, scores high.
    /// </summary>
    internal static double Quality(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', ':', ';', '!', '?', '(', ')', '"', '\'', '-'))
            .Where(t => t.Length > 0).ToList();
        if (tokens.Count < 3) return 0;
        var words = tokens.Count(t => t.Length >= 2 && t.All(char.IsLetter) && t.Any(c => Vowels.Contains(char.ToLowerInvariant(c))));
        var numbers = tokens.Count(t => t.All(c => char.IsDigit(c) || c is '.' or ',' or '/' or ':'));
        return (words + 0.5 * numbers) / tokens.Count;
    }
}
