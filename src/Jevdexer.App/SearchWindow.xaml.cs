using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Jevdexer.Core.Files;
using Jevdexer.Core.Remote;
using Jevdexer.Core.Search;
using Jevdexer.Core.Ultra;

namespace Jevdexer.App;

/// <summary>
/// The command palette. With an empty box it lists recent searches. Otherwise it shows sections,
/// Applications, Folders and Files, narrowed by the scope tabs.
///
/// Apps: local results on every keystroke; after <see cref="PauseBeforeRefine"/> of no typing, and
/// only when the local answer is not confident, Jev refines them. A late answer replaces the list
/// only if the user has not started moving through it.
/// Files and folders: name search on a background thread (the index can be large), local only.
/// Drag any empty area to move the window; the position is remembered.
/// </summary>
public partial class SearchWindow : Window
{
    private enum Scope { All, Apps, Folders, Files }

    private static readonly TimeSpan PauseBeforeRefine = TimeSpan.FromMilliseconds(350);
    private const int RecentShown = 6;

    /// <summary>How far back a tab looks for recent searches of its kind (all of the stored history).</summary>
    private const int SearchHistoryDepth = Jevdexer.Core.History.SearchHistory.MaxStored;

    private readonly ObservableCollection<ResultSection> _sections = [];
    private (int Section, int Row) _cursor = (-1, -1);
    private readonly DispatcherTimer _pause;
    private CancellationTokenSource? _refine;
    private CancellationTokenSource? _fileSearch;
    private List<SearchResult> _apps = [];
    private List<FileHit> _folders = [];
    private List<FileHit> _localFolders = [];
    private List<FileHit> _jevFolders = [];
    private List<ContentHit> _content = [];
    private List<FileHit> _files = [];
    private Scope _scope = Scope.All;
    private bool _userNavigated;
    private bool _refining;
    private bool _offline;
    private bool _rateLimited;
    /// <summary>The key was rejected or has no credits: said until it is fixed (unlike "offline", which is per search).</summary>
    private static string? _keyProblem;
    private int _visibility; // bumps on every show/hide, so a finished fade-out never hides a re-shown window
    private Point _lastMouse;

    private static SearchEngine Engine => App.Instance.Engine;
    private static FileIndex FileIndex => App.Instance.Files;

    public SearchWindow()
    {
        InitializeComponent();
        Sections.ItemsSource = _sections;
        _pause = new DispatcherTimer { Interval = PauseBeforeRefine };
        _pause.Tick += async (_, _) =>
        {
            _pause.Stop();
            await RefineAsync(Query.Text);
        };
        Deactivated += (_, _) => HideSearch();
        PreviewKeyDown += OnKey;
        App.Instance.StateChanged += UpdateStatus;
        Search();
        UpdateStatus();
    }

    // ---------------------------------------------------------------- show, hide, move

    public void ShowSearch()
    {
        _visibility++;
        PlaceWindow();
        if (Query.Text.Length == 0) Search(); // recent searches may have changed
        Show();
        Activate();
        Motion.In(Surface);
        Query.Focus();
        Query.SelectAll();
    }

    public void HideSearch()
    {
        _refine?.Cancel();
        if (!IsVisible) return;
        var version = ++_visibility;
        Motion.Out(Surface, () =>
        {
            if (version == _visibility) Hide();
        });
    }

    /// <summary>The remembered position if it is still on a screen, else centred near the top.</summary>
    private void PlaceWindow()
    {
        var s = App.Instance.Settings;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (s.SearchLeft is { } left && s.SearchTop is { } top && screen.Contains(new Point(left + Width / 2, top + 60)))
        {
            Left = left;
            Top = top;
            FitHeight();
            return;
        }
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.16;
        FitHeight();
    }

    /// <summary>
    /// The window is as tall as the tallest the surface can get, down to the screen's bottom; the
    /// result list is capped to match, so the surface never runs off the screen.
    /// </summary>
    private void FitHeight()
    {
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Height = Math.Clamp(bottom - Top, 320, 860);
        Results.MaxHeight = Math.Clamp(Height - 240, 120, 620); // 240: margins, input, tabs, footer
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only empty areas reach here: the input, list rows and buttons handle their own clicks.
        DragMove();
        App.Instance.RememberSearchPosition(Left, Top);
        Query.Focus();
    }

    // ---------------------------------------------------------------- searching

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        _refine?.Cancel();
        _pause.Stop();
        _userNavigated = false;
        _offline = false;
        _rateLimited = false;
        Search();
        UpdateStatus();
    }

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        _scope = sender == ScopeApps ? Scope.Apps : sender == ScopeFolders ? Scope.Folders : sender == ScopeFiles ? Scope.Files : Scope.All;
        if (!IsLoaded) return;
        _userNavigated = false;
        Compose();
        Query.Focus();
    }

    /// <summary>Apps now (in memory, fast); files and folders on a background thread; then Compose.</summary>
    private void Search()
    {
        var query = Query.Text;
        _fileSearch?.Cancel();
        if (query.Length == 0)
        {
            _apps = [];
            _folders = [];
            _localFolders = [];
            _jevFolders = [];
            _files = [];
            _content = [];
            Compose();
            return;
        }

        _apps = Engine.Local(query);
        _jevFolders = [];
        if (Engine.NeedsRemote(query, _apps) || App.Instance.SmartFoldersOn || App.Instance.UltraRefineOn) _pause.Start(); // RefineAsync decides per kind
        Compose();

        var cts = _fileSearch = new CancellationTokenSource();
        var entries = FileIndex.Entries;
        var ultra = App.Instance.Ultra;
        Task.Run(() => (FileMatcher.Search(entries, query, folders: true, 12), FileMatcher.Search(entries, query, folders: false, 12),
                // Inside files (Ultra Search): local, free; 3+ letters, or it matches noise.
                query.Trim().Length >= 3 ? ultra.Search(query) : []), cts.Token)
            .ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion || query != Query.Text) return;
                (_localFolders, _files, _content) = t.Result;
                _folders = FolderSummaries.Merge(_localFolders, _jevFolders);
                if (!_userNavigated) Compose();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task RefineAsync(string query)
    {
        // Apps and folders are judged in parallel; each is asked only when the search needs it.
        var askApps = Engine.NeedsRemote(query, _apps);
        var askFolders = WantsSmartFolders(query);
        var askContent = WantsSmartContent(query);
        if (!askApps && !askFolders && !askContent) return;

        _refine = new CancellationTokenSource();
        var ct = _refine.Token;
        _refining = true;
        UpdateStatus();
        try
        {
            // In parallel, and independent: each answer shows as soon as it lands, and one that
            // fails (a folder or content call) never throws away another that worked (the apps).
            await Task.WhenAll(
                Apply(askApps ? Engine.RefineAsync(query, ct) : null, r => _apps = r),
                Apply(askFolders ? App.Instance.RefineFoldersAsync(query, ct) : null, r =>
                {
                    _jevFolders = r;
                    _folders = FolderSummaries.Merge(_localFolders, _jevFolders);
                }),
                Apply(askContent ? App.Instance.RefineContentAsync(query, _content, ct) : null, r => _content = r));
        }
        finally
        {
            _refining = false;
            UpdateStatus();
        }

        async Task Apply<T>(Task<T>? task, Action<T> apply)
        {
            if (task is null) return;
            try
            {
                var result = await task;
                if (ct.IsCancellationRequested || query != Query.Text) return;
                apply(result);
                if (!_userNavigated) Compose();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // superseded by the next keystroke
            }
            catch (JevRateLimitedException)
            {
                if (query == Query.Text) _rateLimited = true;
            }
            catch (JevKeyException)
            {
                _keyProblem = "Your AI Gateway key was not accepted: check it in Settings";
            }
            catch (JevCreditsException)
            {
                _keyProblem = "Your AI Gateway credits are used up: matching on this PC";
            }
            catch (Exception e) when (e is HttpRequestException or JevException or TaskCanceledException)
            {
                if (query == Query.Text) _offline = true;
            }
        }
    }

    /// <summary>
    /// Folder Smart search is for descriptions ("tax papers from 2023"), not names: two words or
    /// more, and no folder already matching by name almost exactly.
    /// </summary>
    private bool WantsSmartFolders(string query) =>
        App.Instance.SmartFoldersOn
        && _scope is Scope.All or Scope.Folders
        && query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2
        && !(_localFolders.FirstOrDefault() is { Score: >= 0.9 });

    /// <summary>
    /// Ultra Search asks Jev about descriptions ("differential equations exam"), 2+ words, on the
    /// tabs that show content matches. Counted against the monthly cap.
    /// </summary>
    private bool WantsSmartContent(string query) =>
        App.Instance.UltraRefineOn
        && _scope is Scope.All or Scope.Files or Scope.Folders
        && query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    /// <summary>
    /// Builds the sections: recent searches, or the kinds the scope allows. All shows three rows
    /// of each and scrolls inside a section for the rest (up to 12); a single-kind tab shows more.
    /// Sections and rows are updated in place, so results refreshing while typing do not flicker.
    /// </summary>
    private void Compose()
    {
        var wanted = new List<(string Key, HeaderItem Header, List<object> Rows, int Visible)>();
        if (Query.Text.Length == 0)
        {
            // The tabs filter recent searches by what they led to, like they filter results.
            var recent = App.Instance.History.Recent(SearchHistoryDepth)
                .Where(e => _scope == Scope.All || KindOf(e.AppId) == _scope)
                .Take(RecentShown)
                .Select(e => (object)new RecentItem(e, RecentIcon(e.AppId)))
                .ToList();
            var title = _scope switch
            {
                Scope.Apps => "Recent app searches",
                Scope.Folders => "Recent folder searches",
                Scope.Files => "Recent file searches",
                _ => "Recent searches",
            };
            if (recent.Count > 0) wanted.Add(("recent", new HeaderItem(title, Clearable: true), recent, RecentShown));
        }
        else
        {
            var all = _scope == Scope.All;
            var visible = all ? 3 : 9;
            void Add(string title, IEnumerable<object?> rows)
            {
                var list = rows.OfType<object>().Take(12).ToList();
                if (list.Count > 0) wanted.Add((title, new HeaderItem(title, Count: list.Count > visible ? list.Count : 0), list, visible));
            }
            if (all || _scope == Scope.Apps) Add("Applications", _apps.Select(ToItem));
            if (all || _scope == Scope.Folders) Add("Folders", _folders.Select(ToItem));
            if (all || _scope == Scope.Files) Add("Files", _files.Select(ToItem));

            // Ultra Search: matched by what is inside, minus what a name match already shows.
            var shown = _folders.Select(f => f.Entry.Path).Concat(_files.Select(f => f.Entry.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Add("Found inside", _content
                .Where(h => !shown.Contains(h.Path) && (all || (_scope == Scope.Files && !h.IsFolder) || (_scope == Scope.Folders && h.IsFolder)))
                .Take(12).Select(ToItem));
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var (key, header, rows, visible) = wanted[i];
            var existing = _sections.Skip(i).FirstOrDefault(x => x.Key == key);
            if (existing is null)
            {
                existing = new ResultSection(key);
                _sections.Insert(i, existing);
            }
            else if (_sections.IndexOf(existing) != i) _sections.Move(_sections.IndexOf(existing), i);
            existing.Header = header;
            existing.VisibleRows = visible;
            existing.Sync(rows);
        }
        while (_sections.Count > wanted.Count) _sections.RemoveAt(_sections.Count - 1);

        Results.Visibility = _sections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectFirst();
        UpdateEmpty();
    }

    /// <summary>The subtitle is what the app does when the catalog says so, else who makes it.</summary>
    private static object ToItem(SearchResult r) =>
        new ResultItem(r, r.App.Entity.Name, r.App.Entity.Enrichment?.Description ?? r.App.Entity.Publisher ?? "", IconCache.Get(r.App.Entity));

    private static object ToItem(FileHit h) =>
        new FileItem(h.Entry, IconCache.ForPath(h.Entry.Path, h.Entry.IsFolder, h.Entry.Extension));

    /// <summary>A content match as a file row; null if the file is gone since it was scanned.</summary>
    private static object? ToItem(ContentHit h)
    {
        FileSystemInfo info = h.IsFolder ? new DirectoryInfo(h.Path) : new FileInfo(h.Path);
        if (!info.Exists) return null;
        var ext = h.IsFolder ? "" : info.Extension.TrimStart('.').ToLowerInvariant();
        var entry = new FileEntry
        {
            Path = h.Path, Name = info.Name, IsFolder = h.IsFolder, Modified = info.LastWriteTimeUtc, Extension = ext,
            Key = LocalMatcher.Normalize(Path.GetFileNameWithoutExtension(info.Name)),
        };
        return new FileItem(entry, IconCache.ForPath(h.Path, h.IsFolder, ext), h.Why);
    }

    /// <summary>Whether a history entry led to an app, a folder or a file (Scope.All: led to nothing).</summary>
    private static Scope KindOf(string? id)
    {
        if (id is null) return Scope.All;
        if (!id.StartsWith(RecentItem.PathPrefix, StringComparison.Ordinal)) return Scope.Apps;
        return Directory.Exists(id[RecentItem.PathPrefix.Length..]) ? Scope.Folders : Scope.Files;
    }

    private static ImageSource? RecentIcon(string? id)
    {
        if (id is null) return null;
        if (id.StartsWith(RecentItem.PathPrefix, StringComparison.Ordinal))
        {
            var path = id[RecentItem.PathPrefix.Length..];
            var isFolder = Directory.Exists(path);
            return isFolder || File.Exists(path) ? IconCache.ForPath(path, isFolder, Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) : null;
        }
        return Engine.Find(id) is { } app ? IconCache.Get(app.Entity) : null;
    }

    private void UpdateEmpty()
    {
        Empty.Text = Query.Text.Length == 0
            ? _scope switch
            {
                Scope.Apps => "No recent app searches. Type an app's name, or what you want to do.",
                Scope.Folders => "No recent folder searches. Type a folder's name.",
                Scope.Files => "No recent file searches. Type a file's name, or words from its folder.",
                _ => "Type an app, a folder or a file, or what you want to do, like \"edit a video\".",
            }
            : _refining || !FileIndex.Ready ? "" : "No results found.";
        var show = _sections.Count == 0 && Empty.Text.Length > 0;
        if (show && EmptyPanel.Visibility != Visibility.Visible) Motion.Fade(EmptyPanel, 1);
        EmptyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) EmptyMascot.LookAt(0, Query.Text.Length == 0 ? -0.4 : 0.9); // up at the box, or down at nothing
    }

    /// <summary>Forget a rejected-key or no-credits message (a key was saved or removed).</summary>
    public static void ClearKeyProblem() => _keyProblem = null;

    /// <summary>The footer: what the search is doing, most urgent first.</summary>
    private void UpdateStatus()
    {
        var app = App.Instance;
        var s = app.Status;
        var (icon, text, busy) =
            _refining ? ("Icon.LoaderCircle", "Understanding your search...", true)
            : _keyProblem is { } problem && app.JevOn ? ("Icon.WifiOff", problem, false)
            : _rateLimited ? ("Icon.Sparkles", "Smart search is resting for a moment: showing matches from this PC", false)
            : _offline ? ("Icon.WifiOff", "Offline: showing matches from this PC", false)
            : s.Running ? ("Icon.RefreshCw", s.Progress ?? "Indexing...", true)
            : !FileIndex.Ready ? ("Icon.RefreshCw", "Indexing folders and files...", true)
            : app.Ultra.Status is { Scanning: true, Total: > 0 } u ? ("Icon.ScanSearch", $"Ultra Search: reading {u.Done:N0} of {u.Total:N0} files", true)
            : app.Settings.GatewayKey.Length == 0 ? ("Icon.Sparkles", "Add a key in Settings for Smart search", false)
            : !app.JevOn ? ("Icon.WifiOff", "Smart search is off: matching names only", false)
            : s.Progress is { } warning ? ("Icon.WifiOff", warning, false)
            : ("Icon.Sparkles", $"{s.Apps} apps  ·  {FileIndex.FolderCount:N0} folders  ·  {FileIndex.FileCount:N0} files", false);
        // No spinning icon while busy: progress is shown in words only.
        StatusIcon.Data = (Geometry)FindResource(icon);
        StatusIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        Status.Margin = new Thickness(busy ? 0 : 7, 0, 0, 0);
        Status.Text = text;
        UpdateEmpty();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ClearHistory();
        Compose();
        Query.Focus();
    }

    // ---------------------------------------------------------------- keys and mouse

    private object? SelectedItem =>
        _cursor.Section >= 0 && _cursor.Section < _sections.Count && _cursor.Row >= 0 && _cursor.Row < _sections[_cursor.Section].Rows.Count
            ? _sections[_cursor.Section].Rows[_cursor.Row]
            : null;

    private void SelectFirst() => Select(_sections.Count > 0 ? (0, 0) : (-1, -1));

    /// <summary>One selection across all sections: the cursor's row is selected, every other list has none.</summary>
    private void Select((int Section, int Row) cursor)
    {
        _cursor = cursor;
        for (var i = 0; i < _sections.Count; i++)
        {
            if (_sections[i].View is not { } view) continue;
            view.SelectedIndex = i == cursor.Section ? cursor.Row : -1;
            if (i == cursor.Section && cursor.Row >= 0 && cursor.Row < _sections[i].Rows.Count) view.ScrollIntoView(_sections[i].Rows[cursor.Row]);
        }
    }

    /// <summary>Moves by one row; past a section's end it continues in the next section.</summary>
    private void MoveSelection(int delta)
    {
        var (sec, row) = _cursor;
        if (sec < 0) return;
        row += delta;
        while (sec >= 0 && sec < _sections.Count && (row < 0 || row >= _sections[sec].Rows.Count))
        {
            if (row < 0)
            {
                if (--sec < 0) return;
                row = _sections[sec].Rows.Count - 1;
            }
            else
            {
                if (++sec >= _sections.Count) return;
                row = 0;
            }
        }
        Select((sec, row));
        _sections[sec].View?.BringIntoView(); // the section itself, when the whole list scrolls
        _userNavigated = true;
    }

    private void Section_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox { Tag: ResultSection section } view) return;
        section.View = view;
        var i = _sections.IndexOf(section);
        view.SelectedIndex = i == _cursor.Section ? _cursor.Row : -1;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (Query.Text.Length > 0) Query.Clear();
                else HideSearch();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when _sections.Count > 0:
                MoveSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter when SelectedItem is { } item:
                Choose(item, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
            case Key.Tab:
                var tabs = new[] { ScopeAll, ScopeApps, ScopeFolders, ScopeFiles };
                var next = (Array.FindIndex(tabs, t => t.IsChecked == true) + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 3 : 1)) % 4;
                tabs[next].IsChecked = true;
                Query.Focus();
                e.Handled = true;
                break;
            case Key.OemComma when Keyboard.Modifiers == ModifierKeys.Control:
                OpenSettings();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Hover selects, as in cmdk, but only when the mouse really moved (not when rows change under it).</summary>
    private void Section_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if ((p - _lastMouse).Length < 1) return;
        _lastMouse = p;
        if (sender is ListBox { Tag: ResultSection section } view
            && ItemsControl.ContainerFromElement(view, e.OriginalSource as DependencyObject) is ListBoxItem row)
        {
            var cursor = (_sections.IndexOf(section), view.ItemContainerGenerator.IndexFromContainer(row));
            if (cursor != _cursor) Select(cursor);
            _userNavigated = true;
        }
    }

    /// <summary>Opens the row under the mouse (not the keyboard cursor: a click without a move has not updated it).</summary>
    private void Section_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not ListBox { Tag: ResultSection section } view
            || ItemsControl.ContainerFromElement(view, e.OriginalSource as DependencyObject) is not ListBoxItem row) return;
        Select((_sections.IndexOf(section), view.ItemContainerGenerator.IndexFromContainer(row)));
        if (SelectedItem is { } item) Choose(item, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        HideSearch();
        App.Instance.ShowSettings();
    }

    /// <summary>Opens the chosen row. <paramref name="reveal"/> (Ctrl): show a file or folder in Explorer instead.</summary>
    private void Choose(object item, bool reveal)
    {
        string? error = null;
        switch (item)
        {
            case RecentItem recent:
                Query.Text = recent.Query;
                Query.CaretIndex = Query.Text.Length;
                return;

            case ResultItem result:
                var entity = result.Result.App.Entity;
                if (Launcher.Launch(entity, out error))
                {
                    Done(entity.Id, entity.Name);
                    return;
                }
                break;

            case FileItem file:
                if (Launcher.OpenPath(file.Entry.Path, file.Entry.IsFolder, reveal, out error))
                {
                    Done(RecentItem.PathPrefix + file.Entry.Path, file.Entry.Name);
                    return;
                }
                break;
        }
        StatusIcon.Data = (Geometry)FindResource("Icon.X");
        StatusIcon.Visibility = Visibility.Visible;
        Status.Margin = new Thickness(7, 0, 0, 0);
        Status.Text = error ?? "Could not open that";

        void Done(string id, string name)
        {
            App.Instance.History.Add(Query.Text, id, name);
            Query.Clear();
            HideSearch();
        }
    }
}
