using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Jevdexer.App;

/// <summary>
/// Development: renders a window to a PNG without showing it on the desktop, then exits.
///   Jevdexer.exe --snapshot search|settings|ultra|tour|menu --out file.png [--page general|smart|ultra|privacy] [--step 1-7] [--theme light|dark] [--query "..."]
///                [--then-theme dark --after 130]  (capture partway through a theme crossfade)
/// The window is placed far off-screen and never activated, so it neither flashes nor takes focus
/// from whatever the user is doing. No hotkey, tray icon, indexing, or single-instance lock.
/// </summary>
internal static class Snapshot
{
    public static bool Requested(string[] args) => args.Contains("--snapshot");

    public static async Task RunAsync(App app, string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var surface = Arg("--snapshot") ?? "search";
        var output = Arg("--out") ?? Path.Combine(Path.GetTempPath(), $"jevdexer-{surface}.png");
        var query = Arg("--query");

        // `--ultra-folder <path>`: add a folder to Ultra Search (throwaway profile!) and wait for its scan.
        app.Ultra.Load();
        if (Arg("--ultra-folder") is { } folder)
        {
            app.Ultra.Options.Folders.Add(new Jevdexer.Core.Ultra.UltraFolder { Path = folder });
            app.Ultra.Options.PauseOnBattery = false;
            app.Ultra.StartScan();
            await Task.Delay(500);
            for (var waited = 0; app.Ultra.Status.Scanning && waited < 300; waited++) await Task.Delay(1000);
        }

        Window window = surface switch
        {
            "settings" => new SettingsWindow(Arg("--page") ?? "general"),
            "ultra" => new SettingsWindow("ultra"),
            "tour" => new TourWindow(),
            "menu" => TrayMenu.Create(),
            _ => app.Search!,
        };
        window.ShowActivated = false;
        window.Left = window.Top = -30000;
        window.Show();
        if (window is TourWindow tour && int.TryParse(Arg("--step"), out var step)) tour.GoTo(step);
        if (window is SearchWindow search)
        {
            if (query is not null) search.Query.Text = query;
            // `--scope apps|folders|files`: pick a tab.
            var tab = Arg("--scope") switch { "apps" => search.ScopeApps, "folders" => search.ScopeFolders, "files" => search.ScopeFiles, _ => null };
            if (tab is not null) tab.IsChecked = true;
        }

        // Long enough for icons and a Jev refine to land.
        await Task.Delay(query is not null || surface == "settings" ? 3500 : 800);

        // `--then-theme dark --after 130`: switch theme (without saving) and capture mid-crossfade.
        if (Arg("--then-theme") is { } next)
        {            Theme.Apply(app.Resources, next);
            await Task.Delay(int.TryParse(Arg("--after"), out var ms) ? ms : 130);
        }
        window.UpdateLayout();
        Save(window, output);
        Console.WriteLine($"saved {output}");
    }

    /// <summary>Renders at 2x on a neutral backdrop, so the shadow and borders read in either theme.</summary>
    private static void Save(Window window, string path)
    {
        const double scale = 2;
        var w = window.ActualWidth;
        var h = window.ActualHeight;
        // A window taller than its content (the search window keeps one height): crop to the content.
        if (window.Content is FrameworkElement { VerticalAlignment: VerticalAlignment.Top } top)
            h = Math.Min(h, top.ActualHeight + top.Margin.Top + top.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backdrop = Theme.IsDark ? Color.FromRgb(0x2A, 0x2A, 0x2E) : Color.FromRgb(0xE9, 0xE9, 0xEC);
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush((Visual)window.Content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h) }, null, new Rect(0, 0, w, h));
        }

        var bitmap = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
