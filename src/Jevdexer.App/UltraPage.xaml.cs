using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Jevdexer.Core.Ultra;

namespace Jevdexer.App;

/// <summary>One selected folder on the Ultra Search page.</summary>
public sealed record UltraFolderRow(string Path, string Name, string Detail, bool LocalOnly);

/// <summary>
/// The Ultra Search page of Settings: an empty state with one action; then the folders
/// (each added after its estimate, personal-looking ones private by default), reading progress
/// with pause, "Understand photos and meaning" (Full mode) and the monthly limit for Jev.
/// Wiping lives on the Privacy page. Every change applies at once.
/// </summary>
public partial class UltraPage : UserControl
{
    private bool _loading = true;
    private double _fraction;
    private string? _pending; // folder waiting for "Add"

    private static UltraService Ultra => App.Instance.Ultra;

    public UltraPage()
    {
        InitializeComponent();
        (Ultra.Options.MonthlyCap switch { 0 => CapOff, <= 100 => Cap100, <= 300 => Cap300, _ => Cap1000 }).IsChecked = true;
        Loaded += (_, _) =>
        {
            App.Instance.StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => App.Instance.StateChanged -= Refresh;
        _loading = false;
    }

    public void Refresh()
    {
        var o = Ultra.Options;
        var status = Ultra.Status;
        var index = Ultra.Index;
        var any = o.Folders.Count > 0;

        EmptyCard.Visibility = any || Confirm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        foreach (var e in new FrameworkElement[] { FoldersHeader, ProgressPanel, OptionsSeparator, FullRow, CapRow })
            e.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        FolderList.ItemsSource = o.Folders.Select(f =>
        {
            var c = index.RootCounts.GetValueOrDefault(f.Path);
            var detail = !Directory.Exists(f.Path) ? "Not found (drive disconnected?)" : $"{c.Documents:N0} documents  ·  {c.Photos:N0} photos";
            if (index.SensitiveRoots.ContainsKey(f.Path)) detail += "  ·  has private files";
            return new UltraFolderRow(f.Path, ShortPath(f.Path), detail, f.LocalOnly);
        }).ToList();

        ProgressText.Text = status.Error is { } error ? error
            : o.Paused ? "Paused"
            : status.Scanning && status.Total > 0 ? $"Reading {status.Done:N0} of {status.Total:N0} files"
            : status.Scanning ? "Checking for new files..."
            : "Up to date";
        _fraction = status.Scanning && status.Total > 0 ? status.Done / (double)status.Total : 0;
        ProgressTrack.Visibility = status.Scanning ? Visibility.Visible : Visibility.Collapsed;
        ProgressFill.Width = ProgressTrack.ActualWidth * _fraction;
        PauseText.Text = o.Paused ? "Resume" : "Pause";
        PauseIcon.Data = (Geometry)FindResource(o.Paused ? "Icon.Play" : "Icon.Pause");

        _loading = true;
        FullSwitch.IsChecked = o.Full;
        _loading = false;
        FullHelp.Text = status.Downloading
            ? $"Downloading: {status.Downloaded / 1_000_000} of {ModelStore.TotalBytes / 1_000_000} MB"
            : o.Full ? "On. Runs on this PC." : $"Also finds photos (\"beach\", \"wedding\"). One-time {ModelStore.TotalBytes / 1_000_000} MB download.";

        var smartOff = !App.Instance.Settings.SmartSearch;
        CapTabs.IsEnabled = !smartOff;
        CapHelp.Text = smartOff ? "Needs Smart search." : o.MonthlyCap == 0 ? "Off: stays on this PC." : $"For hard searches, per month. {o.UsedNow} used.";
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) => ProgressFill.Width = e.NewSize.Width * _fraction;

    // ---------------------------------------------------------------- folders

    /// <summary>Opens the folder picker; the tour calls this too.</summary>
    public async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for Ultra Search" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dialog.FolderName;
        if (Ultra.Options.Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        if (Path.GetPathRoot(path) == path)
        {
            ShowConfirm(path, "Pick a folder, not a whole drive.", allowStart: false);
            return;
        }

        _pending = path;
        ShowConfirm(path, "Counting files...", allowStart: false);
        var o = Ultra.Options;
        var plan = await Task.Run(() => Ultra.Plan([new UltraFolder { Path = path }]));
        if (_pending != path) return;
        var seconds = plan.Seconds(o.Full, o.PassageMs ?? 150, o.PhotoMs ?? 230);
        ShowConfirm(path, $"{plan.Documents:N0} documents and {plan.Photos:N0} photos. About {Duration(seconds)}, in the background while you are away.",
            allowStart: plan.Documents + plan.Photos > 0);
        SensitivePanel.Visibility = UltraService.LooksSensitive(path) ? Visibility.Visible : Visibility.Collapsed;
        ConfirmLocalOnly.IsChecked = true;
    }

    private void ShowConfirm(string path, string estimate, bool allowStart)
    {
        ConfirmTitle.Text = $"Add {ShortPath(path)}?";
        ConfirmEstimate.Text = estimate;
        ConfirmStart.IsEnabled = allowStart;
        SensitivePanel.Visibility = Visibility.Collapsed;
        Confirm.Visibility = Visibility.Visible;
        EmptyCard.Visibility = Visibility.Collapsed;
        Motion.Fade(Confirm, 1);
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        _pending = null;
        Confirm.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void ConfirmStart_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } path) return;
        var localOnly = SensitivePanel.Visibility == Visibility.Visible && ConfirmLocalOnly.IsChecked == true;
        _pending = null;
        Confirm.Visibility = Visibility.Collapsed;
        Ultra.AddFolder(path, localOnly);
        Refresh();
    }

    private void LocalOnly_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string path } toggle) Ultra.SetLocalOnly(path, toggle.IsChecked == true);
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) _ = Task.Run(() => Ultra.RemoveFolder(path));
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        var paused = !Ultra.Options.Paused;
        _ = Task.Run(() => Ultra.SetPaused(paused));
    }

    // ---------------------------------------------------------------- options

    private async void Full_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (FullSwitch.IsChecked != true)
        {
            Ultra.UseLight();
            Refresh();
            return;
        }
        FullSwitch.IsEnabled = false;
        try
        {
            await Ultra.EnableFullAsync();
            Refresh();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or InvalidOperationException)
        {
            _loading = true;
            FullSwitch.IsChecked = false;
            _loading = false;
            FullHelp.Text = ex is SmartSearchOffException
                ? "Needs a one-time download, and Smart search is off. Turn Smart search on first."
                : "Could not download right now. Try again later.";
        }
        finally
        {
            FullSwitch.IsEnabled = true;
        }
    }

    private void Cap_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not FrameworkElement { Tag: string cap }) return;
        Ultra.Options.MonthlyCap = int.Parse(cap);
        App.Instance.Settings.Save();
        Refresh();
    }

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string ShortPath(string path) =>
        path.StartsWith(Profile, StringComparison.OrdinalIgnoreCase) ? "~" + path[Profile.Length..] : path;

    private static string Duration(double seconds) =>
        seconds < 90 ? "a minute" : seconds < 3600 ? $"{Math.Round(seconds / 60)} minutes" : $"{seconds / 3600:0.#} hours";
}
