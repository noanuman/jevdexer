using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jevdexer.App;

/// <summary>
/// The first-run tour: seven short steps with a picture each, from what Jevdexer finds to trying
/// the shortcut for real. Choices can be made on the way (Smart search, folders, Ultra Search); the
/// last step waits for the user to press their shortcut. Shown once on first start
/// (<c>Settings.TourDone</c>), again from Settings, "Take the tour".
/// </summary>
public partial class TourWindow : Window
{
    private sealed record Step(FrameworkElement Panel, string Title, Func<string> Text, string Next = "Next", string? Secondary = null);
    public sealed record Dot(double Width, Brush Brush);

    private readonly Step[] _steps;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private int _index;
    private int _tick;
    private bool _closing;
    private bool _openUltra; // "Set up now" on the Ultra Search step: open that page after the tour

    private static string Keys => string.Join(" + ", App.Keys(App.Instance.Settings.Hotkey));

    public TourWindow()
    {
        InitializeComponent();
        _steps =
        [
            new(Step1, "Welcome to Jevdexer", () => "Find any app, folder or file on this PC, by its name or by what it does.", "Show me"),
            new(Step2, "Open it from anywhere", () => $"Press {Keys} in any app to search. Esc closes it again. You can change the shortcut in Settings."),
            new(Step3, "Type a name, or what you need", () => "\"steam\" finds Steam. \"edit a video\" finds your video editors, even if you forgot their names."),
            new(Step4, "Narrow it down", () => "Results are grouped into apps, folders and files. Use the keyboard to move fast."),
            new(Step5, "Smart search", () => "Optional: paste a free Vercel AI Gateway key and Jevdexer understands what you mean. It uses your own account; Jevdexer has no server and never sees your files. Skip it and everything else still works."),
            new(Step6, "Ultra Search", () => "Optional: search inside documents, scans and photos in folders you choose. They are read on this PC.", "Later", "Set up now"),
            new(Step7, "Try it now", () => $"Press {Keys}. That's all there is to it.", "Open search"),
        ];

        Keys2.Content = App.Keys(App.Instance.Settings.Hotkey);
        Keys7.Content = App.Keys(App.Instance.Settings.Hotkey);
        SyncSmart();
        App.Instance.StateChanged += SyncSmart; // a key saved in the box turns Smart search on

        _anim.Tick += (_, _) => Animate();
        App.Instance.HotkeyPressed += OnHotkey;
        Closed += (_, _) =>
        {
            _anim.Stop();
            Explainer.Close();
            App.Instance.HotkeyPressed -= OnHotkey;
            App.Instance.StateChanged -= SyncSmart;
            Finish();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseAnimated();
            else if (e.Key is Key.Right or Key.Enter && _index < _steps.Length - 1) Go(_index + 1);
            else if (e.Key == Key.Left && _index > 0) Go(_index - 1);
        };
        Loaded += (_, _) => Motion.In(Surface);
        Go(0);
    }

    /// <summary>Shows step 1 to 7 (snapshots).</summary>
    public void GoTo(int step) => Go(Math.Clamp(step - 1, 0, _steps.Length - 1));

    private void Go(int index)
    {
        _index = index;
        var step = _steps[index];
        foreach (var s in _steps) s.Panel.Visibility = s == step ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) Motion.Fade(step.Panel, 1);

        StepTitle.Text = step.Title;
        StepText.Text = step.Text();
        NextText.Text = step.Next;
        NextIcon.Visibility = index < _steps.Length - 1 && step.Secondary is null ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Visibility = step.Secondary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryText.Text = step.Secondary ?? "";
        BackButton.Visibility = index == 0 ? Visibility.Hidden : Visibility.Visible;
        SkipButton.Visibility = index == _steps.Length - 1 ? Visibility.Hidden : Visibility.Visible;

        var on = (Brush)FindResource("Foreground");
        var off = (Brush)FindResource("Border");
        Dots.ItemsSource = _steps.Select((_, i) => new Dot(i == index ? 20 : 6, i == index ? on : off)).ToList();

        _tick = 0;
        var video = step.Panel == Step3 && PlayExplainer();
        if (step.Panel != Step3) Explainer.Stop();
        if ((step.Panel == Step3 && !video) || step.Panel == Step4) _anim.Start();
        else _anim.Stop();
    }

    // ---------------------------------------------------------------- the explainer video

    private static readonly string ExplainerPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Tour", "explainer.mp4");
    private bool _videoFailed;

    /// <summary>Shows and plays the recording from the start; false when there is none (the mock shows instead).</summary>
    private bool PlayExplainer()
    {
        if (_videoFailed || !System.IO.File.Exists(ExplainerPath)) return false;
        Explainer.Source ??= new Uri(ExplainerPath);
        VideoFrame.Visibility = Visibility.Visible;
        MockSearch.Visibility = Visibility.Collapsed;
        Explainer.Position = TimeSpan.Zero;
        Explainer.Play();
        return true;
    }

    private void Explainer_MediaEnded(object sender, RoutedEventArgs e)
    {
        Explainer.Position = TimeSpan.Zero; // loop
        Explainer.Play();
    }

    /// <summary>No H.264 decoder (Windows N editions) or a damaged file: back to the drawn mock.</summary>
    private void Explainer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _videoFailed = true;
        VideoFrame.Visibility = Visibility.Collapsed;
        MockSearch.Visibility = Visibility.Visible;
        if (_steps[_index].Panel == Step3) _anim.Start();
    }

    /// <summary>Rounded corners: a Border does not clip its child, so clip the video to the frame.</summary>
    private void VideoFrame_SizeChanged(object sender, SizeChangedEventArgs e) =>
        VideoClip.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);

    /// <summary>Step 3 (without the video) types two searches; step 4 walks the tabs.</summary>
    private void Animate()
    {
        _tick++;
        if (_steps[_index].Panel == Step3)
        {
            string[] queries = ["edit a video", "steam"];
            var cycle = _tick % 90;
            var query = queries[_tick / 90 % queries.Length];
            MockQuery.Text = query[..Math.Min(query.Length, Math.Max(0, cycle - 8) / 2)];
            Caret.Opacity = _tick / 5 % 2 == 0 ? 1 : 0;
        }
        else if (_tick % 14 == 0)
        {
            var tabs = new[] { MockAll, MockApps, MockFolders, MockFiles };
            tabs[_tick / 14 % tabs.Length].IsChecked = true;
        }
    }

    // ---------------------------------------------------------------- choices on the way

    private void TourSmart_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.SetSmartSearch(TourSmart.IsChecked == true);
        SyncSmart();
    }

    /// <summary>Smart search needs a key: the switch waits for one.</summary>
    private void SyncSmart()
    {
        var app = App.Instance;
        var hasKey = app.Settings.GatewayKey.Length > 0;
        TourSmart.IsEnabled = hasKey;
        TourSmart.IsChecked = app.JevOn;
        TourSmartRow.Opacity = hasKey ? 1 : 0.5;
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ---------------------------------------------------------------- navigation

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _steps.Length - 1) Go(_index + 1);
        else CloseAnimated(() => App.Instance.Search!.ShowSearch());
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _openUltra = true; // opened when the tour ends, so the tour still finishes with "try it"
        Go(_index + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_index > 0) Go(_index - 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => CloseAnimated();

    /// <summary>The shortcut works: on the last step that ends the tour (search is already opening).</summary>
    private void OnHotkey()
    {
        if (_index != _steps.Length - 1) return;
        WaitingText.Text = "That's it.";
        CloseAnimated();
    }

    private void Finish()
    {
        var settings = App.Instance.Settings;
        if (!settings.TourDone)
        {
            settings.TourDone = true;
            settings.Save();
        }
        if (_openUltra) Dispatcher.BeginInvoke(() => App.Instance.ShowSettings("ultra"), DispatcherPriority.ApplicationIdle);
    }

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

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
