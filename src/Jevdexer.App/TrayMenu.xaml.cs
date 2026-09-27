using System.Windows;
using Forms = System.Windows.Forms;

namespace Jevdexer.App;

/// <summary>
/// The tray's right-click menu, drawn as a WPF dropdown instead of the WinForms context menu.
/// Opens above the cursor, kept inside the work area, and closes when it loses focus.
/// </summary>
public partial class TrayMenu : Window
{
    private TrayMenu()
    {
        InitializeComponent();
        var app = App.Instance;
        SearchKeys.Content = App.Keys(app.Settings.Hotkey);
        var s = app.Status;
        Summary.Text = $"{s.Apps} apps indexed" + (app.Settings.SmartSearch ? "" : "  ·  Smart search off");
        Reindex.IsEnabled = !s.Running;
        ReindexIcon.Spin = s.Running;
        ReindexText.Text = s.Running ? "Indexing..." : "Re-index apps";
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => CloseOnce();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) CloseOnce();
        };
    }

    private bool _closing;

    /// <summary>
    /// Closing deactivates the window, and Deactivated closes it: without this guard a click on an item
    /// calls Close while the window is already closing, which throws and takes the app down.
    /// </summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Motion.Out(Surface, Close);
    }

    /// <summary>For <see cref="Snapshot"/>: the menu without placing or showing it.</summary>
    internal static TrayMenu Create() => new();

    public static void ShowAtCursor()
    {
        var menu = new TrayMenu();
        menu.Left = menu.Top = -10000; // measure off-screen first, then place: no flash at 0,0
        menu.Show();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(menu);
        var cursor = Forms.Cursor.Position;
        var area = SystemParameters.WorkArea;
        var x = cursor.X / dpi.DpiScaleX - menu.ActualWidth + 16;
        var y = cursor.Y / dpi.DpiScaleY - menu.ActualHeight + 16;
        menu.Left = Math.Clamp(x, area.Left, area.Right - menu.ActualWidth);
        menu.Top = Math.Clamp(y, area.Top, area.Bottom - menu.ActualHeight);
        menu.Activate();
        Motion.In(menu.Surface, rise: -4, scale: 0.97);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        CloseOnce();
        App.Instance.Search!.ShowSearch();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CloseOnce();
        App.Instance.ShowSettings();
    }

    private void Reindex_Click(object sender, RoutedEventArgs e)
    {
        CloseOnce();
        App.Instance.StartIndexing(manual: true);
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Instance.Shutdown();
}
