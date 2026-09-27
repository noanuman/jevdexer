namespace Jevdexer.Core.Files;

/// <summary>
/// Keeps a <see cref="FileIndex"/> current with Windows' change notifications, one watcher per
/// root. Events are collected and applied after a short quiet period, so saving a file (which
/// fires several events) costs one folder refresh. A buffer overflow (thousands of changes at
/// once) triggers a full rebuild instead of guessing.
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(800);

    private readonly FileIndex _index;
    private readonly FileRoots _roots;
    private readonly Action _rebuild;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _refresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;

    /// <param name="rebuild">Called when the watcher lost track (buffer overflow).</param>
    public FileWatcher(FileIndex index, FileRoots roots, Action rebuild)
    {
        _index = index;
        _roots = roots;
        _rebuild = rebuild;
        _timer = new Timer(_ => Flush());
        foreach (var root in roots.Roots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => OnCreated(e.FullPath);
                w.Deleted += (_, e) => OnDeleted(e.FullPath);
                w.Changed += (_, e) => Queue(_refresh, Path.GetDirectoryName(e.FullPath));
                w.Renamed += (_, e) =>
                {
                    OnDeleted(e.OldFullPath);
                    OnCreated(e.FullPath);
                };
                w.Error += (_, _) => _rebuild();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // An unwatchable root (a drive removed, a network share): it stays as last crawled.
            }
        }
    }

    private void OnCreated(string path)
    {
        if (Directory.Exists(path)) Queue(_added, path);
        Queue(_refresh, Path.GetDirectoryName(path));
    }

    private void OnDeleted(string path)
    {
        Queue(_removed, path); // harmless if it was a file: no folder has that path
        Queue(_refresh, Path.GetDirectoryName(path));
    }

    private void Queue(HashSet<string> set, string? path)
    {
        // Most notifications come from folders the index never holds (AppData, Temp, caches):
        // drop them here by name; the index re-checks attributes when it applies the rest.
        if (path is null || !_roots.Covers(path, checkAttributes: false)) return;
        lock (_refresh)
        {
            set.Add(path);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] removed, added, refresh;
        lock (_refresh)
        {
            removed = [.. _removed];
            added = [.. _added];
            refresh = [.. _refresh];
            _removed.Clear();
            _added.Clear();
            _refresh.Clear();
        }
        try
        {
            foreach (var r in removed) _index.RemoveTree(r);
            foreach (var a in added) _index.AddTree(a);
            foreach (var f in refresh) _index.RefreshFolder(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Raced with more changes; the next event refreshes again.
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
