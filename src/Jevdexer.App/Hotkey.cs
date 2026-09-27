using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Jevdexer.App;

/// <summary>
/// One global hotkey through `RegisterHotKey`. Never a keyboard hook: a hook would see
/// every keystroke on the machine. Registration fails when another app holds the combination;
/// the caller must tell the user, never fail silently.
/// </summary>
internal sealed partial class Hotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int Id = 0x4A45; // "JE"

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _registered;

    public string Text { get; }

    private Hotkey(HwndSource source, string text, Action pressed)
    {
        _source = source;
        Text = text;
        _pressed = pressed;
    }

    /// <summary>Registers "Win+Alt+Space"-style text. Returns null and a reason when it cannot.</summary>
    public static (Hotkey? Hotkey, string? Error) Register(HwndSource source, string text, Action pressed)
    {
        if (!TryParse(text, out var modifiers, out var vk)) return (null, $"'{text}' is not a key combination");

        var hotkey = new Hotkey(source, text, pressed);
        if (!RegisterHotKey(source.Handle, Id, modifiers | ModNoRepeat, vk))
            return (null, $"{text} is already used by another app");

        hotkey._registered = true;
        source.AddHook(hotkey.WndProc);
        return (hotkey, null);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    internal static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        foreach (var mod in parts[..^1])
        {
            modifiers |= mod.ToLowerInvariant() switch
            {
                "win" => ModWin,
                "alt" => ModAlt,
                "ctrl" or "control" => ModControl,
                "shift" => ModShift,
                _ => 0,
            };
        }
        if (modifiers == 0 || !Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(WndProc);
        _registered = false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
