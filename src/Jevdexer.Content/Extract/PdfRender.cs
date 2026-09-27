using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Jevdexer.Content.Extract;

/// <summary>
/// Renders PDF pages with Windows' own PDF engine (Windows.Data.Pdf, the one Edge and the Photos
/// app use), for pages whose text is drawn as shapes, scanned or handwritten: PdfPig sees no words
/// there. The images go to OCR, and (Full mode) to the photo tagger. Sandboxed worker only.
/// </summary>
public static class PdfRender
{
    private const uint RenderWidth = 1800; // enough for OCR of slide text; below OCR's max dimension

    /// <summary>PNGs of the given pages (0-based). Empty for encrypted or damaged files.</summary>
    public static async Task<List<(int Index, byte[] Png)>> RenderAsync(byte[] bytes, IReadOnlyList<int> pageIndexes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        var results = new List<(int, byte[])>();
        PdfDocument doc;
        try
        {
            doc = await PdfDocument.LoadFromStreamAsync(stream);
        }
        catch (Exception)
        {
            return results; // keep whatever PdfPig read
        }

        foreach (var index in pageIndexes)
        {
            if (index >= doc.PageCount) continue;
            using var page = doc.GetPage((uint)index);
            var png = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(png, new PdfPageRenderOptions
            {
                DestinationWidth = RenderWidth,
                BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            });
            var image = new byte[png.Size];
            png.Seek(0);
            await png.ReadAsync(image.AsBuffer(), (uint)image.Length, InputStreamOptions.None);
            results.Add((index, image));
        }
        return results;
    }
}
