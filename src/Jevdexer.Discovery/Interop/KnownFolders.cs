using System.Runtime.InteropServices;

namespace Jevdexer.Discovery.Interop;

/// <summary>Resolves `{GUID}\rest` paths, which the AppsFolder uses for apps under known folders.</summary>
internal static partial class KnownFolders
{
    public static string? Expand(string path)
    {
        if (!path.StartsWith('{')) return path;
        var close = path.IndexOf('}');
        if (close < 0 || !Guid.TryParse(path[..(close + 1)], out var id)) return null;

        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try
        {
            var root = Marshal.PtrToStringUni(ptr);
            return root is null ? null : root + path[(close + 1)..];
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
