using System.Windows;
using System.Windows.Input;

namespace Jevdexer.App;

/// <summary>
/// Settings, in four pages on a sidebar: General (shortcut, theme, sign-in), Smart search (the
/// switch, folders, what goes online), Ultra Search (<see cref="UltraPage"/>), Privacy and data
/// (history, Ultra data, index). One short line per setting. Every change applies and saves
/// immediately; there is no Save button.
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _recording;
    private bool _loading = true;
    private bool _closing;
    private DateTime _wipeArmedUntil; // the wipe needs a second click within a few seconds (no modal dialogs)

    private static readonly Dictionary<string, (string Title, string Subtitle)> Pages = new()
    {
        ["general"] = ("General", "How Jevdexer opens and looks."),
        ["smart"] = ("Smart search", "Search by what you mean, not only by name."),
        ["ultra"] = ("Ultra Search", "Search inside the folders you choose."),
        ["privacy"] = ("Privacy & data", "What Jevdexer keeps on this PC."),
    };

    /// <param name="page">"general", "smart", "ultra" or "privacy".</param>
    public SettingsWindow(string page = "general")
    {
        InitializeComponent();
        var app = App.Instance;

        (app.Settings.Theme switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        StartupSwitch.IsChecked = app.Settings.StartWithWindows && Installation.IsInstalled;
        StartupSwitch.IsEnabled = Installation.IsInstalled;
        if (!Installation.IsInstalled) StartupHelp.Text = "Only in the installed app.";
        VersionText.Text = $"Version {Installation.Version}" + (Installation.IsInstalled ? "" : " (dev)");
        VersionText.ToolTip = "Inter font (OFL) and Lucide icons (ISC)";
        ShortcutKeys.Content = App.Keys(app.Settings.Hotkey);

        app.StateChanged += Refresh;
        Closed += (_, _) =>
        {
            app.StateChanged -= Refresh;
            if (_recording) app.ResumeHotkey();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (_recording) StopRecording();
        };

        Loaded += (_, _) => Motion.In(Surface);
        ShowPage(page);
        Refresh();
        _loading = false;
    }

    /// <summary>Switches the page (the tour and the tray use this too).</summary>
    public void ShowPage(string page)
    {
        (page switch { "smart" => NavSmart, "ultra" => NavUltra, "privacy" => NavPrivacy, _ => NavGeneral }).IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string page }) return;
        PageGeneral.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageSmart.Visibility = page == "smart" ? Visibility.Visible : Visibility.Collapsed;
        PageUltra.Visibility = page == "ultra" ? Visibility.Visible : Visibility.Collapsed;
        PagePrivacy.Visibility = page == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = Pages[page];
        if (IsLoaded) Motion.Fade(page switch { "smart" => PageSmart, "ultra" => PageUltra, "privacy" => PagePrivacy, _ => PageGeneral }, 1);
    }

    /// <summary>Fades out, then closes. Every close path goes through here.</summary>
    private void CloseAnimated(Action? then = null)
    {
        if (_closing) return;
        _closing = true;
        Motion.Out(Surface, () =>
        {
            Close();
            then?.Invoke();
        });
    }

    private void Refresh()
    {
        var app = App.Instance;
        var s = app.Status;
        var files = app.Files;

        // Smart search: needs the user's key
        var hasKey = app.Settings.GatewayKey.Length > 0;
        SmartSwitch.IsEnabled = hasKey;
        SmartSwitch.IsChecked = app.JevOn;
        SmartRow.Opacity = hasKey ? 1 : 0.5;
        SmartFoldersSwitch.IsEnabled = app.JevOn;
        SmartFoldersSwitch.IsChecked = app.JevOn && app.Settings.SmartFolders;
        SmartFoldersRow.Opacity = app.JevOn ? 1 : 0.5;
        SmartPrivacy.Text = !hasKey
            ? "Without a key nothing goes online. Search matches names, and Ultra Search matches contents, on this PC."
            : app.JevOn
                ? "Goes straight to Vercel's AI Gateway with your key: app names and your search" + (app.Settings.SmartFolders ? ", and short folder summaries" : "") + ". Never your files. Jevdexer has no server."
                : "Off: nothing goes online. Search matches names only.";

        // Privacy & data
        var n = app.History.Count;
        HistorySummary.Text = n == 0 ? "Empty." : $"{n} recent {(n == 1 ? "search" : "searches")}.";
        ClearHistoryButton.IsEnabled = n > 0;

        var ultra = app.Ultra;
        UltraSummary.Text = ultra.Index.IsEmpty && !ultra.Enabled
            ? "Nothing yet."
            : $"{ultra.Index.Documents:N0} documents and {ultra.Index.Photos:N0} photos understood.";
        WipeButton.IsEnabled = ultra.Enabled || !ultra.Index.IsEmpty;

        IndexSummary.Text = $"{s.Apps} apps"
                            + (files.Ready ? $", {files.FolderCount:N0} folders, {files.FileCount:N0} files" : ", folders and files loading")
                            + (s.LastRun is { } t ? $". Updated {Ago(t)}." : ".");
        ReindexButton.IsEnabled = !s.Running;
        ReindexText.Text = s.Running ? "Indexing..." : "Re-index";
    }

    // ---------------------------------------------------------------- actions

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is FrameworkElement { Tag: string mode }) App.Instance.SetTheme(mode);
    }

    private void Smart_Click(object sender, RoutedEventArgs e) => App.Instance.SetSmartSearch(SmartSwitch.IsChecked == true);

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void SmartFolders_Click(object sender, RoutedEventArgs e) => App.Instance.SetSmartFolders(SmartFoldersSwitch.IsChecked == true);

    private void Reindex_Click(object sender, RoutedEventArgs e) => App.Instance.StartIndexing(manual: true);

    private void Startup_Click(object sender, RoutedEventArgs e) => App.Instance.SetStartWithWindows(StartupSwitch.IsChecked == true);

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => App.Instance.ClearHistory();

    private void Wipe_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow > _wipeArmedUntil)
        {
            _wipeArmedUntil = DateTime.UtcNow.AddSeconds(5);
            WipeText.Text = "Click again";
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(() => WipeText.Text = "Wipe"));
            return;
        }
        _wipeArmedUntil = DateTime.MinValue;
        WipeText.Text = "Wipe";
        _ = Task.Run(() => App.Instance.Ultra.Wipe(models: false));
    }

    private void Tour_Click(object sender, RoutedEventArgs e) => CloseAnimated(App.Instance.ShowTour);

    private void Search_Click(object sender, RoutedEventArgs e) => CloseAnimated(() => App.Instance.Search!.ShowSearch());

    // ---------------------------------------------------------------- shortcut recording

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
            return;
        }
        _recording = true;
        App.Instance.SuspendHotkey(); // pressing the current shortcut must record it, not open search
        ShortcutKeys.Visibility = Visibility.Collapsed;
        ShortcutRecording.Visibility = Visibility.Visible;
        ShortcutHelp.Text = "Press the new keys. Esc cancels.";
        ShortcutError.Visibility = Visibility.Collapsed;
    }

    /// <param name="registered">True when ChangeHotkey already registered a shortcut (the new one, or the old one back).</param>
    private void StopRecording(string? error = null, bool registered = false)
    {
        _recording = false;
        if (!registered) App.Instance.ResumeHotkey();
        ShortcutKeys.Content = App.Keys(App.Instance.Settings.Hotkey);
        ShortcutKeys.Visibility = Visibility.Visible;
        ShortcutRecording.Visibility = Visibility.Collapsed;
        ShortcutHelp.Text = "Opens search anywhere. Click to change.";
        ShortcutError.Text = error ?? "";
        ShortcutError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            if (e.Key == Key.Escape) CloseAnimated();
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // wait for the non-modifier key

        var parts = new List<string>();
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (parts.Count == 0)
        {
            ShortcutError.Text = "Add Win, Ctrl, Alt or Shift.";
            ShortcutError.Visibility = Visibility.Visible;
            return;
        }
        parts.Add(key.ToString());

        var error = App.Instance.ChangeHotkey(string.Join("+", parts));
        StopRecording(error is null ? null : $"{error}. Kept {App.Instance.Settings.Hotkey.Replace("+", " + ")}.", registered: true);
    }

    // ---------------------------------------------------------------- window

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only empty areas reach here: buttons, switches and tabs handle their own clicks.
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : t.ToString("d MMM");
    }
}
