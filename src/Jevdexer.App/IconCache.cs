using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jevdexer.Discovery;

namespace Jevdexer.App;

/// <summary>
/// App icons as the shell draws them, through IShellItemImageFactory: one call covers Store
/// apps (`shell:AppsFolder\&lt;AUMID&gt;`) and exes alike. Cached per entity; null when the shell
/// has none (launcher games with only a URI), and the row shows a letter instead.
/// </summary>
internal static partial class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Get(Entity e) => Cache.GetOrAdd(e.Id, _ => Load(e));

    /// <summary>Extensions whose icon differs per file; every other file type shares one icon per extension.</summary>
    private static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase) { "exe", "lnk", "ico", "url", "appref-ms" };

    /// <summary>
    /// The icon Explorer shows for a file or folder. Cached per extension (and once for folders),
    /// so a list of results asks the shell for a handful of icons, not one per file.
    /// </summary>
    public static ImageSource? ForPath(string path, bool isFolder, string extension)
    {
        var key = isFolder ? "folder:" : OwnIcon.Contains(extension) ? "file:" + path : "ext:" + extension;
        return Cache.GetOrAdd(key, _ => LoadPath(path));
    }

    private static ImageSource? Load(Entity e)
    {
        var path = e.Aumid is not null ? $@"shell:AppsFolder\{e.Aumid}"
            : e.Exe is not null && File.Exists(e.Exe) ? e.Exe
            : e.IconPath is not null && File.Exists(e.IconPath) ? e.IconPath
            : null;
        return path is null ? null : LoadPath(path);
    }

    private static ImageSource? LoadPath(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        try
        {
            if (factory.GetImage(new Size32(64, 64), SiigbfIconOnly | SiigbfBiggerSizeOk, out var hbitmap) != 0) return null;
            try { return FromHBitmap(hbitmap); }
            finally { DeleteObject(hbitmap); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the 32-bit DIB with its alpha channel. `Imaging.CreateBitmapSourceFromHBitmap`
    /// would drop the alpha and paint icons on black.
    /// </summary>
    private static unsafe BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        Bitmap bm;
        if (GetObjectW(hbitmap, sizeof(Bitmap), &bm) == 0 || bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) return null;

        var stride = bm.bmWidthBytes;
        var pixels = new byte[stride * bm.bmHeight];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

        // DIB sections from the shell are bottom-up: flip rows.
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < bm.bmHeight; y++)
            Buffer.BlockCopy(pixels, y * stride, flipped, (bm.bmHeight - 1 - y) * stride, stride);

        var source = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, flipped, stride);
        source.Freeze();
        return source;
    }

    private const int SiigbfBiggerSizeOk = 0x1, SiigbfIconOnly = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Size32(int Cx, int Cy);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size32 size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetObjectW(IntPtr h, int c, void* pv);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr ho);
}
