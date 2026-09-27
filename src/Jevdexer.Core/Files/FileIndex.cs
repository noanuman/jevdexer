using System.Diagnostics;

namespace Jevdexer.Core.Files;

/// <summary>
/// The file index: in memory only, never written to disk. Entries are grouped by their
/// parent folder so a change notification refreshes one folder, not the whole index. Built in the
/// background by <see cref="BuildAsync"/>, kept current by <see cref="FileWatcher"/>.
/// Thread-safe: searches read an immutable snapshot, updates swap in a new one.
/// </summary>
public sealed class FileIndex
{
    private readonly object _write = new();
    private Dictionary<string, List<FileEntry>> _byParent = new(StringComparer.OrdinalIgnoreCase);
    private FileEntry[] _snapshot = [];

    public FileRoots? Rules { get; private set; }
    public bool Ready { get; private set; }
    public int FileCount { get; private set; }
    public int FolderCount { get; private set; }
    public TimeSpan LastBuild { get; private set; }

    /// <summary>Raised after a build or an update, from a background thread.</summary>
    public event Action? Changed;

    /// <summary>All entries. Callers must not mutate it.</summary>
    public IReadOnlyList<FileEntry> Entries => Volatile.Read(ref _snapshot);

    public async Task BuildAsync(FileRoots rules, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var parts = await Task.WhenAll(rules.Roots.Select(root => Task.Run(() => FileCrawler.Crawl(root, rules, recursive: true, ct), ct)));

        var merged = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
            foreach (var (parent, entries) in part)
                merged[parent] = entries;

        lock (_write)
        {
            Rules = rules;
            _byParent = merged;
            Publish();
            LastBuild = sw.Elapsed;
            Ready = true;
        }
        progress?.Report(FileCount + FolderCount);
        Changed?.Invoke();
    }

    /// <summary>Re-reads one folder's direct children (after a create, delete or rename inside it).</summary>
    public void RefreshFolder(string folder)
    {
        if (Rules is not { } rules || !rules.Covers(folder)) return;
        var fresh = Directory.Exists(folder) ? FileCrawler.Crawl(folder, rules, recursive: false) : [];
        lock (_write)
        {
            var next = new Dictionary<string, List<FileEntry>>(_byParent, StringComparer.OrdinalIgnoreCase);
            next.Remove(folder);
            foreach (var (parent, entries) in fresh) next[parent] = entries;
            _byParent = next;
            Publish();
        }
        Changed?.Invoke();
    }

    /// <summary>Adds a new folder with everything in it (after a folder is created or renamed into place).</summary>
    public void AddTree(string folder)
    {
        if (Rules is not { } rules || !rules.Covers(folder) || !Directory.Exists(folder)) return;
        var fresh = FileCrawler.Crawl(folder, rules, recursive: true);
        lock (_write)
        {
            var next = new Dictionary<string, List<FileEntry>>(_byParent, StringComparer.OrdinalIgnoreCase);
            foreach (var (parent, entries) in fresh) next[parent] = entries;
            _byParent = next;
            Publish();
        }
        Changed?.Invoke();
    }

    /// <summary>Forgets a folder and everything under it (after it is deleted or renamed away).</summary>
    public void RemoveTree(string folder)
    {
        lock (_write)
        {
            var prefix = folder.TrimEnd('\\') + "\\";
            var next = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (parent, entries) in _byParent)
            {
                if (string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                next[parent] = entries;
            }
            _byParent = next;
            Publish();
        }
        Changed?.Invoke();
    }

    /// <summary>Rebuilds the flat snapshot searches read. Called under the write lock.</summary>
    private void Publish()
    {
        var all = new FileEntry[_byParent.Values.Sum(l => l.Count)];
        var i = 0;
        var folders = 0;
        foreach (var list in _byParent.Values)
            foreach (var e in list)
            {
                all[i++] = e;
                if (e.IsFolder) folders++;
            }
        FolderCount = folders;
        FileCount = all.Length - folders;
        Volatile.Write(ref _snapshot, all);
    }
}
