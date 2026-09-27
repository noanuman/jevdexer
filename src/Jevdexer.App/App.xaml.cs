using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using Jevdexer.Core;
using Jevdexer.Core.Files;
using Jevdexer.Core.History;
using Jevdexer.Core.Indexing;
using Jevdexer.Core.Remote;
using Jevdexer.Core.Search;
using Jevdexer.Core.Store;
using Jevdexer.Core.Ultra;
using Forms = System.Windows.Forms;

namespace Jevdexer.App;

/// <summary>Index state for the settings window and the tray menu.</summary>
public sealed record IndexStatus(int Apps, int Understood, DateTimeOffset? LastRun, bool Running, string? Progress);

/// <summary>
/// Tray app and the controller the windows talk to: settings, theme, hotkey, Smart search, indexing.
/// The store is touched by one piece of work at a time: the startup load, then the indexing task,
/// which reloads the engine on the UI thread when it finishes.
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _single;
    private IndexStore? _store;
    /// <summary>Jev with the user's own key; null while Smart search is off or there is no key (then nothing goes online).</summary>
    private JevClient? _backend;
    private HwndSource? _hotkeySource;
    private Hotkey? _hotkey;
    private Forms.NotifyIcon? _tray;
    private Task? _indexing;
    private Task? _fileIndexing;
    private FileWatcher? _fileWatcher;
    private DateTime _lastFileNotice;
    private IndexStatus _status = new(0, 0, null, false, null);

    public static App Instance => (App)Current;

    public Settings Settings { get; private set; } = new();
    public SearchEngine Engine { get; private set; } = new(null);
    public SearchWindow? Search { get; private set; }
    public SearchHistory History { get; private set; } = null!;

    /// <summary>Folders and files, held in memory only.</summary>
    public FileIndex Files { get; } = new();

    /// <summary>Ultra Search: content of the folders the user picked. Idle until a folder is added.</summary>
    public UltraService Ultra { get; private set; } = null!;
    private System.Windows.Threading.DispatcherTimer? _ultraTrim;
    public IndexStatus Status => _status;

    /// <summary>Raised on the UI thread when index status, Smart search or the hotkey changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when the user presses the search shortcut (the tour's last step listens).</summary>
    public event Action? HotkeyPressed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => LogCrash(ex.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash(ex.ExceptionObject as Exception);
        var snapshot = Snapshot.Requested(e.Args);
        if (!snapshot)
        {
            _single = new Mutex(true, @"Local\Jevdexer.SingleInstance", out var first);
            if (!first)
            {
                Shutdown();
                return;
            }
        }

        Settings = Settings.Load();
        var themeArg = Array.IndexOf(e.Args, "--theme") is var t and >= 0 && t + 1 < e.Args.Length ? e.Args[t + 1] : null;
        Theme.Apply(Resources, themeArg ?? Settings.Theme);
        Theme.FollowSystem(Resources);

        History = SearchHistory.OpenDefault();
        _store = IndexStore.OpenDefault();
        _backend = JevClient.For(Settings);
        Engine = new SearchEngine(_backend);
        var apps = _store.LoadApps();
        Engine.Load(apps, TagCatalog.List);
        _status = new IndexStatus(apps.Count, apps.Count(a => a.Label is not null), null, false, null);

        Ultra = new UltraService(Settings);
        Ultra.Changed += () => Dispatcher.BeginInvoke(() => StateChanged?.Invoke());

        Search = new SearchWindow();
        if (snapshot)
        {
            Dispatcher.BeginInvoke(async () =>
            {
                await Files.BuildAsync(FileRoots.Default(FileRoots.AppFolders(apps)));
                StateChanged?.Invoke();
                await Snapshot.RunAsync(this, e.Args);
                Shutdown();
            });
            return;
        }

        _hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(Search).EnsureHandle());
        var error = RegisterHotkey(Settings.Hotkey);

        _tray = CreateTray();
        if (error is not null)
        {
            _tray.ShowBalloonTip(8000, "Jevdexer shortcut unavailable",
                $"{error}. Open Settings from the tray icon to pick another.", Forms.ToolTipIcon.Warning);
        }

        Files.Changed += OnFilesChanged;
        StartFileIndexing(apps);
        StartIndexing();
        StartUltra();
        Installation.SyncStartup(Settings.StartWithWindows);
        _ = CheckForUpdateAsync();

        // Development: `Jevdexer.exe --open search|settings|menu` shows that surface on start.
        var open = Array.IndexOf(e.Args, "--open") is var i and >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        Dispatcher.BeginInvoke(() =>
        {
            if (open == "search") Search.ShowSearch();
            else if (open == "settings") ShowSettings();
            else if (open == "menu") TrayMenu.ShowAtCursor();
            else if (open == "tour" || !Settings.TourDone) ShowTour(); // first start: the tour
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ---------------------------------------------------------------- Ultra Search

    /// <summary>Loads the content index and resumes scanning, in the background; frees the query model when idle.</summary>
    private void StartUltra()
    {
        _ = Task.Run(() =>
        {
            Ultra.Load();
            Ultra.StartScan(); // picks up changes made while the app was closed
        });
        _ultraTrim = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _ultraTrim.Tick += (_, _) => Ultra.TrimMemory(TimeSpan.FromMinutes(5));
        _ultraTrim.Start();
    }

    /// <summary>Content results may be refined by Jev: Smart search on, folders chosen, cap not zero.</summary>
    public bool UltraRefineOn => _backend is not null && Ultra.Enabled && Ultra.Options.MonthlyCap > 0;

    public Task<List<ContentHit>> RefineContentAsync(string query, List<ContentHit> local, CancellationToken ct) =>
        UltraRefineOn ? Ultra.RefineAsync(_backend!, query, local, ct) : Task.FromResult(local);

    // ---------------------------------------------------------------- settings

    public void SetStartWithWindows(bool on)
    {
        Settings.StartWithWindows = on;
        Settings.Save();
        Installation.SyncStartup(on);
    }

    private async Task CheckForUpdateAsync()
    {
        if (await Installation.DownloadUpdateAsync(Settings.UpdateUrl) is { } version)
            _tray?.ShowBalloonTip(6000, "Jevdexer update ready", $"Version {version} installs the next time Jevdexer starts.", Forms.ToolTipIcon.Info);
    }

    public void ClearHistory()
    {
        History.Clear();
        StateChanged?.Invoke();
    }

    public void RememberSearchPosition(double left, double top)
    {
        Settings.SearchLeft = left;
        Settings.SearchTop = top;
        Settings.Save();
    }

    public void SetTheme(string mode)
    {
        Settings.Theme = mode;
        Settings.Save();
        Theme.Apply(Resources, mode);
    }

    public void SetSmartSearch(bool on)
    {
        Settings.SmartSearch = on;
        Settings.Save();
        ConnectJev();
    }

    /// <summary>Saves the user's AI Gateway key (empty removes it) and reconnects.</summary>
    public void SetGatewayKey(string key)
    {
        Settings.GatewayKey = key;
        Settings.Save();
        ConnectJev();
    }

    /// <summary>A new Jev client for the current switch and key, a new engine on it, and labels for what is new.</summary>
    private void ConnectJev()
    {
        SearchWindow.ClearKeyProblem();
        _backend?.Dispose();
        _backend = JevClient.For(Settings);
        Engine = new SearchEngine(_backend);
        Engine.Load(_store!.LoadApps(), TagCatalog.List);
        StateChanged?.Invoke();
        if (_backend is not null) StartIndexing(); // label anything that arrived while it was off
    }

    /// <summary>Smart search can go online: on, with a key.</summary>
    public bool JevOn => _backend is not null;

    public void SetSmartFolders(bool on)
    {
        Settings.SmartFolders = on;
        Settings.Save();
        StateChanged?.Invoke();
    }

    /// <summary>Folder Smart search is live: both switches on.</summary>
    public bool SmartFoldersOn => _backend is not null && Settings.SmartFolders;

    /// <summary>Jev's probability per shortlisted folder for a descriptive search; empty when off or nothing qualifies.</summary>
    public Task<List<FileHit>> RefineFoldersAsync(string query, CancellationToken ct) =>
        SmartFoldersOn && Files.Ready
            ? FolderSummaries.RefineAsync(_backend!, Files.Entries, query, ct)
            : Task.FromResult(new List<FileHit>());

    /// <summary>Registers <paramref name="text"/>; on failure the previous shortcut stays. Returns an error or null.</summary>
    public string? ChangeHotkey(string text)
    {
        var previous = Settings.Hotkey;
        _hotkey?.Dispose();
        var error = RegisterHotkey(text);
        if (error is not null)
        {
            RegisterHotkey(previous);
            return error;
        }
        Settings.Hotkey = text;
        Settings.Save();
        StateChanged?.Invoke();
        return null;
    }

    /// <summary>While the settings window records a new shortcut, the current one must not fire.</summary>
    public void SuspendHotkey() => _hotkey?.Dispose();
    public void ResumeHotkey() => RegisterHotkey(Settings.Hotkey);

    private string? RegisterHotkey(string text)
    {
        var (hotkey, error) = Hotkey.Register(_hotkeySource!, text, () =>
        {
            Search!.ShowSearch();
            HotkeyPressed?.Invoke();
        });
        _hotkey = hotkey;
        return error;
    }

    public static IReadOnlyList<string> Keys(string hotkey) =>
        hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // ---------------------------------------------------------------- indexing and Smart search

    /// <param name="manual">Started by the user (Re-index): folders and files are rebuilt too.</param>
    public void StartIndexing(bool manual = false)
    {
        if (_indexing is { IsCompleted: false }) return;
        var indexer = new Indexer(_store!, _backend);
        var progress = new Progress<string>(p => SetStatus(_status with { Running = true, Progress = p }));
        SetStatus(_status with { Running = true, Progress = "Finding apps..." });

        var firstRun = _status.Apps == 0; // the file crawl could not yet skip the apps' folders
        _indexing = Task.Run(async () =>
        {
            var result = await indexer.RunAsync(progress);
            var apps = _store!.LoadApps();
            var tags = TagCatalog.List;
            await Dispatcher.InvokeAsync(() =>
            {
                Engine.Load(apps, tags);
                var offline = result.LabelError is not null && _backend is not null;
                SetStatus(new IndexStatus(apps.Count, apps.Count(a => a.Label is not null), DateTimeOffset.Now, false,
                    offline ? "Could not reach the AI Gateway; search by name still works" : null));
                if (manual || (firstRun && apps.Count > 0)) StartFileIndexing(apps);
            });
        });
    }

    /// <summary>
    /// Crawls folders and files in the background, then watches them. Install folders of indexed apps
    /// are skipped (their contents are the apps' business). Nothing is written to disk.
    /// </summary>
    public void StartFileIndexing(IReadOnlyList<IndexedApp> apps)
    {
        if (_fileIndexing is { IsCompleted: false }) return;
        var rules = FileRoots.Default(FileRoots.AppFolders(apps));
        _fileWatcher?.Dispose();
        _fileIndexing = Task.Run(async () =>
        {
            await Files.BuildAsync(rules);
            _fileWatcher = new FileWatcher(Files, rules, rebuild: () => Dispatcher.BeginInvoke(() => StartFileIndexing(Engine.Apps)));
        });
        StateChanged?.Invoke();
    }

    /// <summary>File changes can arrive in bursts: tell the windows at most twice a second.</summary>
    private void OnFilesChanged()
    {
        var now = DateTime.UtcNow;
        if (now - _lastFileNotice < TimeSpan.FromMilliseconds(500)) return;
        _lastFileNotice = now;
        Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }

    /// <summary>Checks the saved key against the Gateway (free: the credit balance).</summary>
    public Task<(JevClient.KeyState State, decimal? Balance)> CheckKeyAsync() => JevClient.CheckKeyAsync(Settings.GatewayKey);

    private void SetStatus(IndexStatus status)
    {
        _status = status;
        StateChanged?.Invoke();
    }

    // ---------------------------------------------------------------- windows and tray

    /// <param name="page">"general", "smart", "ultra" or "privacy".</param>
    public void ShowSettings(string page = "general")
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.ShowPage(page);
            existing.Activate();
            return;
        }
        new SettingsWindow(page).Show();
    }

    public void ShowTour()
    {
        var existing = Windows.OfType<TourWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }
        new TourWindow().Show();
    }

    private Forms.NotifyIcon CreateTray()
    {
        var tray = new Forms.NotifyIcon { Icon = MakeTrayIcon(Theme.TaskbarLight()), Text = "Jevdexer", Visible = true };
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Search!.ShowSearch();
            else if (e.Button == Forms.MouseButtons.Right) TrayMenu.ShowAtCursor();
        };
        return tray;
    }

    /// <summary>
    /// The mascot's silhouette with two eye dots (brand art, tools/brand/make_icons.py): dark on a
    /// light taskbar, light on a dark one, where the black mascot would disappear.
    /// </summary>
    private static System.Drawing.Icon MakeTrayIcon(bool lightTaskbar)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{(lightTaskbar ? "tray-dark" : "tray-light")}.png");
        using var stream = GetResourceStream(uri)!.Stream;
        using var bmp = new Bitmap(stream);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Appends to `%LOCALAPPDATA%\Jevdexer\crash.log`. Local only; never sent anywhere.</summary>
    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            File.AppendAllText(Path.Combine(Settings.DataDir, "crash.log"), $"{DateTimeOffset.Now:O}\n{ex}\n\n");
        }
        catch (IOException)
        {
            // nothing sensible left to do
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _fileWatcher?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _ultraTrim?.Stop();
        Ultra?.Dispose();
        _backend?.Dispose();
        _store?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
