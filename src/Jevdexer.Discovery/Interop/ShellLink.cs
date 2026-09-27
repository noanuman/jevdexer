using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Jevdexer.Discovery.Interop;

/// <summary>Reads the target of a .lnk file through the shell's own IShellLinkW.</summary>
internal static class ShellLink
{
    /// <summary>Target path and arguments. Target is null for advertised (MSI) and non-file shortcuts.</summary>
    public static (string? Target, string? Args) Read(string lnkPath)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!;
        var link = Activator.CreateInstance(type)!;
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var shell = (IShellLinkW)link;
            var sb = new StringBuilder(1024);
            shell.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            sb.Clear();
            shell.GetArguments(sb, sb.Capacity);
            var args = sb.ToString().Trim();
            return (target.Length == 0 ? null : target, args.Length == 0 ? null : args);
        }
        catch (COMException)
        {
            return (null, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        // The remaining methods are declared only to keep the vtable layout; unused.
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
