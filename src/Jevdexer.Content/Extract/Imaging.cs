using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Jevdexer.Content.Extract;

/// <summary>
/// Image decoding with Windows' own codecs (WIC through WinRT): JPEG, PNG, HEIC (with the free
/// HEIF extension), BMP, GIF, TIFF, and camera RAW where a codec is installed. EXIF rotation applied.
/// </summary>
public static class Imaging
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "heic", "heif", "bmp", "gif", "tif", "tiff", "webp", "dng",
    };

    /// <summary>Decodes, scaled so the longer side is at most <paramref name="maxSide"/>.</summary>
    public static async Task<SoftwareBitmap> DecodeAsync(byte[] bytes, uint maxSide)
    {
        var decoder = await BitmapDecoder.CreateAsync(await ToStream(bytes));
        var (w, h) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(w, h));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(w * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(h * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        return await Get(decoder, transform);
    }

    /// <summary>CLIP's input: shortest side to <paramref name="size"/> (bicubic), centre crop to a square.</summary>
    public static async Task<SoftwareBitmap> DecodeSquareAsync(byte[] bytes, uint size)
    {
        var decoder = await BitmapDecoder.CreateAsync(await ToStream(bytes));
        var (w, h) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        var scale = size / (double)Math.Min(w, h);
        var (sw, sh) = (Math.Max(size, (uint)Math.Round(w * scale)), Math.Max(size, (uint)Math.Round(h * scale)));
        var transform = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Cubic,
            Bounds = new BitmapBounds { X = (sw - size) / 2, Y = (sh - size) / 2, Width = size, Height = size },
        };
        return await Get(decoder, transform);
    }

    private static async Task<SoftwareBitmap> Get(BitmapDecoder decoder, BitmapTransform transform) =>
        await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

    /// <summary>BGRA pixels of a Bgra8 bitmap.</summary>
    public static byte[] Pixels(SoftwareBitmap bitmap)
    {
        var buffer = new byte[4 * bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyToBuffer(buffer.AsBuffer());
        return buffer;
    }

    private static async Task<IRandomAccessStream> ToStream(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return stream;
    }
}
