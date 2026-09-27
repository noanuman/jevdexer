using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;

namespace Jevdexer.Content.Extract;

/// <summary>Text pulled out of a document, and how.</summary>
public sealed record ExtractedText(string Text, int Pages, bool UsedOcr, string? Title)
{
    /// <summary>Rendered pages neither the text layer nor OCR could read (handwriting, drawings): for the photo tagger.</summary>
    public List<byte[]> PageImages { get; init; } = [];
}

/// <summary>
/// Plain text from documents: PDF (PdfPig, managed; image-only pages go to OCR), Office Open XML
/// (docx, pptx, xlsx) and OpenDocument (odt, odp, ods) read as zipped XML, EPUB, RTF, HTML and plain
/// text. Everything here parses untrusted bytes, so it runs only in the sandboxed worker,
/// with caps on pages, characters and decompressed sizes (zip bombs).
/// </summary>
public static partial class DocumentText
{
    public const int MaxChars = 60_000;
    private const int MaxPdfPages = 60;
    private const int MaxOcrPages = 8;
    private const long MaxZipEntryBytes = 20 * 1024 * 1024;

    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "docm", "pptx", "xlsx", "odt", "odp", "ods", "epub", "rtf", "html", "htm", "txt", "md", "csv", "tex",
    };

    public static async Task<ExtractedText> ReadAsync(byte[] bytes, string extension) => extension.ToLowerInvariant() switch
    {
        "pdf" => await Pdf(bytes),
        "docx" or "docm" => Zip(bytes, e => e == "word/document.xml", "p"),
        "pptx" => Zip(bytes, e => e.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.EndsWith(".xml", StringComparison.Ordinal), "p", slides: true),
        "xlsx" => Zip(bytes, e => e == "xl/sharedStrings.xml", "si"),
        "odt" or "odp" or "ods" => Zip(bytes, e => e == "content.xml", "p"),
        "epub" => Zip(bytes, e => e.EndsWith(".xhtml", StringComparison.Ordinal) || e.EndsWith(".html", StringComparison.Ordinal), "p"),
        "rtf" => new(Cap(Rtf(Decode(bytes))), 0, false, null),
        "html" or "htm" => new(Cap(Html(Decode(bytes))), 0, false, null),
        _ => new(Cap(Decode(bytes)), 0, false, null),
    };

    // ---------------------------------------------------------------- PDF

    private static async Task<ExtractedText> Pdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var pageCount = pdf.NumberOfPages;
        var pages = Math.Min(pageCount, MaxPdfPages);
        var texts = new List<string>();
        var total = 0;
        for (var n = 1; n <= pages && total < MaxChars; n++)
        {
            texts.Add(string.Join(' ', pdf.GetPage(n).GetWords().Select(w => w.Text)));
            total += texts[^1].Length;
        }

        // Pages without a text layer (scans, or slides whose letters are drawn as outlines or
        // handwritten): render them as a viewer would and read them. A spread of at most
        // MaxOcrPages. Pages OCR cannot read either go back as images for the photo tagger.
        var empty = Enumerable.Range(0, texts.Count).Where(i => texts[i].Length < 40).ToList();
        var ocrPages = 0;
        var unreadable = new List<byte[]>();
        if (empty.Count > 0)
        {
            foreach (var (index, png) in await PdfRender.RenderAsync(bytes, Spread(empty, MaxOcrPages)))
            {
                var text = await Ocr.ReadAsync(png);
                if (text.Length > 0)
                {
                    texts[index] += " " + text;
                    ocrPages++;
                }
                else if (unreadable.Count < MaxTaggedPages) unreadable.Add(png);
            }
        }

        var title = pdf.Information.Title is { Length: > 3 } t ? t.Trim() : null;
        return new(Cap(string.Join('\n', texts)), pageCount, ocrPages > 0, title) { PageImages = unreadable };
    }

    private const int MaxTaggedPages = 3;

    /// <summary>At most <paramref name="max"/> items spread evenly, first and last included.</summary>
    public static List<int> Spread(List<int> items, int max)
    {
        if (items.Count <= max) return items;
        return Enumerable.Range(0, max).Select(i => items[(int)Math.Round(i * (items.Count - 1) / (double)(max - 1))]).Distinct().ToList();
    }

    // ---------------------------------------------------------------- zipped XML

    /// <param name="paragraph">Local name of the element that ends a paragraph (w:p, a:p, text:p, si).</param>
    private static ExtractedText Zip(byte[] bytes, Func<string, bool> wanted, string paragraph, bool slides = false)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => wanted(e.FullName)).ToList();
        if (slides) entries = entries.OrderBy(e => SlideNumber(e.FullName)).ToList();

        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            if (sb.Length >= MaxChars) break;
            if (entry.Length > MaxZipEntryBytes) continue; // a zip bomb, or not a document worth reading
            using var stream = new LimitedStream(entry.Open(), MaxZipEntryBytes);
            XmlText(stream, paragraph, sb);
        }
        var title = zip.GetEntry("docProps/core.xml") is { Length: < 1_000_000 } core ? CoreTitle(core) : null;
        return new(Cap(sb.ToString()), slides ? entries.Count : 0, false, title);
    }

    private static void XmlText(Stream stream, string paragraph, StringBuilder sb)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, MaxCharactersFromEntities = 0 };
        using var reader = XmlReader.Create(stream, settings);
        try
        {
            while (reader.Read() && sb.Length < MaxChars)
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace) sb.Append(reader.Value);
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == paragraph) sb.Append('\n');
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "tab" or "br" or "s") sb.Append(' ');
            }
        }
        catch (XmlException)
        {
            // keep what was read before the damage
        }
    }

    private static string? CoreTitle(ZipArchiveEntry core)
    {
        var sb = new StringBuilder();
        using var reader = XmlReader.Create(new LimitedStream(core.Open(), 1_000_000), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        try
        {
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "title") return reader.ReadElementContentAsString().Trim() is { Length: > 3 } t ? t : null;
        }
        catch (XmlException)
        {
        }
        return null;
    }

    private static int SlideNumber(string name) => int.TryParse(Digits().Match(name).Value, out var n) ? n : 0;

    // ---------------------------------------------------------------- simple formats

    private static string Decode(byte[] bytes)
    {
        var take = bytes.AsSpan(0, Math.Min(bytes.Length, MaxChars * 4));
        if (take.Length >= 2 && take[0] == 0xFF && take[1] == 0xFE) return Encoding.Unicode.GetString(take[2..]);
        if (take.Length >= 2 && take[0] == 0xFE && take[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(take[2..]);
        return new UTF8Encoding(false, false).GetString(take); // also reads plain ASCII; bad bytes become U+FFFD
    }

    private static string Html(string html) =>
        WebUtility.HtmlDecode(Tags().Replace(ScriptOrStyle().Replace(html, " "), " "));

    /// <summary>RTF without a parser: drop groups for fonts, colours and pictures, then control words.</summary>
    private static string Rtf(string rtf)
    {
        var s = RtfSkipGroups().Replace(rtf, " ");
        s = RtfHex().Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        s = RtfUnicode().Replace(s, m => ((char)(short)int.Parse(m.Groups[1].Value)).ToString());
        s = RtfControl().Replace(s, " ");
        return s.Replace("{", "").Replace("}", "");
    }

    private static string Cap(string text)
    {
        var t = Whitespace().Replace(FixSpacingAccents(text), " ").Trim();
        return t.Length <= MaxChars ? t : t[..MaxChars];
    }

    /// <summary>
    /// LaTeX PDFs often store "č" as a spacing caron then "c" ("jednaˇcine"). Joins such pairs into
    /// the real letter: the accent becomes its combining form, then NFC composes.
    /// </summary>
    internal static string FixSpacingAccents(string text)
    {
        if (text.IndexOfAny(['ˇ', '´', '¨', 'ˆ', '˜', '˘', '¸']) < 0) return text;
        var joined = SpacingAccent().Replace(text, m =>
        {
            var combining = m.Groups[1].Value[0] switch
            {
                'ˇ' => '̌', '´' => '́', '¨' => '̈', 'ˆ' => '̂', '˜' => '̃', '˘' => '̆', _ => '̧',
            };
            return m.Groups[2].Value + combining;
        });
        return joined.Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"([ˇ´¨ˆ˜˘¸]) ?(\p{L})")] private static partial Regex SpacingAccent();
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();
    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptOrStyle();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\{\\(fonttbl|colortbl|stylesheet|pict|info|\*)[^{}]*(\{[^{}]*\}[^{}]*)*\}")] private static partial Regex RtfSkipGroups();
    [GeneratedRegex(@"\\'([0-9a-fA-F]{2})")] private static partial Regex RtfHex();
    [GeneratedRegex(@"\\u(-?\d+)\??")] private static partial Regex RtfUnicode();
    [GeneratedRegex(@"\\[a-zA-Z]+-?\d* ?")] private static partial Regex RtfControl();
    [GeneratedRegex(@"[ \t\r\f\v]+")] private static partial Regex Whitespace();

    /// <summary>Stops reading after a byte budget: a zip entry can lie about its size.</summary>
    private sealed class LimitedStream(Stream inner, long budget) : Stream
    {
        private long _left = budget;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
