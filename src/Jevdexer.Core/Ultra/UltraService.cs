using Jevdexer.Content.Models;
using Jevdexer.Content.Worker;
using Jevdexer.Core.Remote;

namespace Jevdexer.Core.Ultra;

/// <summary>What Ultra Search is doing, for the window and the footer.</summary>
public sealed record UltraStatus(bool Scanning, int Done, int Total, string? Folder, bool Downloading, long Downloaded, string? Error);

/// <summary>
/// Ultra Search for the app: the selected folders, background scans, Light and Full mode, the
/// query model, the Jev refinement and the wipe. Nothing is scanned until the user selects folders.
/// </summary>
public sealed class UltraService : IDisposable
{
    /// <summary>A passage slower than this on one core makes Full mode "heavy" for this PC.</summary>
    public const double ComfortablePassageMs = 400;

    private readonly Settings _settings;
    private readonly Action _save;
    private ContentStore _store;
    private CancellationTokenSource? _scan;
    private Task? _scanTask;
    private TextEmbedder? _queryModel;
    private ClipText? _clipModel;
    private float[]? _clipNeutral;
    private DateTime _queryModelUsed;
    private readonly Lock _modelLock = new();

    public ContentIndex Index { get; private set; } = ContentIndex.Empty;
    public UltraStatus Status { get; private set; } = new(false, 0, 0, null, false, 0, null);
    public event Action? Changed;

    public UltraSettings Options => _settings.Ultra;

    public UltraService(Settings settings, Action? save = null)
    {
        _settings = settings;
        _save = save ?? settings.Save;
        _store = ContentStore.OpenDefault();
    }

    public bool Enabled => Options.Folders.Count > 0;

    /// <summary>Loads the stored index; call once at start (off the UI thread).</summary>
    public void Load()
    {
        Index = new ContentIndex(_store.LoadAll(), Options.Folders);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- folders

    /// <summary>Folder names that suggest personal records: such folders start local-only.</summary>
    public static bool LooksSensitive(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(path,
            @"(medical|health|zdravstv|zdravlj|bolnic|lekar|doctor|passport|paso[sš]|licn|lična|identity|\bids?\b|documents? personal|private|privatno|lozink|password|bank|banka|tax|porez|insurance|osiguranj|legal|pravn)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public ScanPlan Plan(IEnumerable<UltraFolder> folders) => ContentScanner.Plan(folders, _store, Options.Mode);

    public void AddFolder(string path, bool localOnly)
    {
        if (Options.Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        Options.Folders.Add(new UltraFolder { Path = path, LocalOnly = localOnly });
        _save();
        StartScan();
    }

    public void RemoveFolder(string path)
    {
        CancelScan();
        Options.Folders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        _store.RemoveRoot(path);
        _save();
        Load();
        if (Enabled) StartScan();
    }

    public void SetLocalOnly(string path, bool localOnly)
    {
        if (Options.Folders.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } folder) return;
        folder.LocalOnly = localOnly;
        if (!localOnly) folder.SensitiveConfirmed = true;
        _save();
        Load();
    }

    // ---------------------------------------------------------------- scanning

    public void StartScan()
    {
        if (!Enabled || Options.Paused || !ContentWorker.Installed) return;
        CancelScan();
        var cts = _scan = new CancellationTokenSource();
        var folders = Options.Folders.ToList();
        var full = Options.Full && ModelStore.Present;
        _scanTask = Task.Run(async () =>
        {
            try
            {
                SetStatus(Status with { Scanning = true, Done = 0, Total = 0, Error = null });
                var plan = ContentScanner.Plan(folders, _store, full ? "full" : "light");
                var progress = new Progress<ContentScanner.Progress>(p => SetStatus(Status with { Done = p.Done, Total = p.Total, Folder = p.Folder }));
                var reloadEvery = 25;
                var sinceReload = 0;
                var reporting = new Progress<ContentScanner.Progress>(p =>
                {
                    ((IProgress<ContentScanner.Progress>)progress).Report(p);
                    if (++sinceReload >= reloadEvery)
                    {
                        sinceReload = 0;
                        _ = Task.Run(Load); // results become searchable while the scan runs
                    }
                });
                await ContentScanner.RunAsync(plan, folders, _store, () => new ContentWorker(full), Options, reporting, cts.Token);
                Load();
                SetStatus(Status with { Scanning = false });
            }
            catch (OperationCanceledException)
            {
                SetStatus(Status with { Scanning = false });
            }
            catch (Exception e)
            {
                // The name only: messages can carry paths.
                SetStatus(Status with { Scanning = false, Error = $"Scanning stopped ({e.GetType().Name}). It resumes on the next start." });
            }
        }, cts.Token);
    }

    public void SetPaused(bool paused)
    {
        Options.Paused = paused;
        _save();
        if (paused) CancelScan();
        else StartScan();
        Changed?.Invoke();
    }

    private void CancelScan()
    {
        _scan?.Cancel();
        try
        {
            _scanTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // cancelled
        }
        _scan = null;
    }

    // ---------------------------------------------------------------- modes

    /// <summary>
    /// Switches to Full: downloads the models if needed (hash-pinned), measures this PC, rescans so
    /// every file gets meaning vectors. Returns the benchmark.
    /// </summary>
    /// <exception cref="SmartSearchOffException">The models are missing and Smart search is off: no download.</exception>
    public async Task<BenchmarkResult> EnableFullAsync(CancellationToken ct = default)
    {
        if (!ModelStore.Present)
        {
            if (!_settings.SmartSearch) throw new SmartSearchOffException();
            SetStatus(Status with { Downloading = true, Downloaded = 0, Error = null });
            try
            {
                await ModelStore.DownloadAsync(new Progress<long>(b => SetStatus(Status with { Downloading = true, Downloaded = b })), ct);
            }
            finally
            {
                SetStatus(Status with { Downloading = false });
            }
        }
        var bench = await Task.Run(Benchmark, ct);
        Options.Mode = "full";
        Options.PassageMs = bench.PassageMs;
        Options.PhotoMs = bench.PhotoMs;
        _save();
        StartScan();
        return bench;
    }

    public void UseLight()
    {
        Options.Mode = "light";
        _save();
        DropQueryModel();
        StartScan();
    }

    /// <summary>Times the models in the sandbox on built-in samples (no user files).</summary>
    public BenchmarkResult Benchmark()
    {
        using var worker = new ContentWorker(full: ModelStore.Present);
        return worker.Benchmark();
    }

    // ---------------------------------------------------------------- search

    /// <summary>Local content matches: keywords always, meaning when Full mode is on. No network.</summary>
    public List<ContentHit> Search(string query)
    {
        if (Index.IsEmpty) return [];
        var vector = Index.HasVectors ? QueryVector(query) : null;
        return Index.Search(query, vector, photo: ClipVector(query));
    }

    /// <summary>
    /// Asks Jev about the best local matches that may leave the PC (not local-only, not sensitive),
    /// as scrubbed summaries with positional ids. Counts against the monthly cap; returns the
    /// input unchanged when the cap is reached or nothing may be sent.
    /// </summary>
    public async Task<List<ContentHit>> RefineAsync(JevClient backend, string query, List<ContentHit> local, CancellationToken ct)
    {
        var vector = Index.HasVectors ? QueryVector(query) : null;
        var candidates = local.Concat(Index.Candidates(query, vector, photo: ClipVector(query))).DistinctBy(h => h.Path, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        var sendable = candidates.Select(h => (Hit: h, Summary: Index.Summary(h))).Where(x => x.Summary is not null).ToList();
        if (sendable.Count == 0 || !Options.TryUse()) return local;
        _save();
        var response = await backend.SearchContentAsync(query, sendable.Select((x, i) => new JevClient.FolderRequest($"c{i}", x.Summary!)).ToList(), ct);
        var byPath = sendable.Select((x, i) => (x.Hit.Path, P: response.Items.GetValueOrDefault($"c{i}", -1)))
            .Where(x => x.P >= 0).ToDictionary(x => x.Path, x => x.P, StringComparer.OrdinalIgnoreCase);
        return ContentIndex.Merge(candidates, byPath);
    }

    private float[]? QueryVector(string query)
    {
        lock (_modelLock)
        {
            if (!ModelStore.Present) return null;
            _queryModel ??= new TextEmbedder(ModelStore.Load("e5-small-int8.onnx"), ModelStore.Load("e5-tokenizer.json"));
            _queryModelUsed = DateTime.UtcNow;
            return _queryModel.EmbedQuery(query);
        }
    }

    /// <summary>The query in CLIP's space, for photos (Full mode with its models); null otherwise.</summary>
    private ClipQuery? ClipVector(string query)
    {
        if (!Index.HasPhotoVectors) return null;
        lock (_modelLock)
        {
            if (!ModelStore.Present) return null;
            if (_clipModel is null)
            {
                _clipModel = new ClipText(ModelStore.Load("clip-text-int8.onnx"), ModelStore.Load("clip-tokenizer.json"));
                _clipNeutral = _clipModel.Embed("a photo");
            }
            _queryModelUsed = DateTime.UtcNow;
            return new ClipQuery(_clipModel.Embed(query), _clipNeutral!);
        }
    }

    /// <summary>The query model holds ~150 MB: let it go after a quiet spell. The app calls this on a timer.</summary>
    public void TrimMemory(TimeSpan idle)
    {
        lock (_modelLock)
        {
            if (_queryModel is not null && DateTime.UtcNow - _queryModelUsed > idle) DropQueryModel();
        }
    }

    private void DropQueryModel()
    {
        lock (_modelLock)
        {
            _queryModel?.Dispose();
            _queryModel = null;
            _clipModel?.Dispose();
            _clipModel = null;
        }
    }

    // ---------------------------------------------------------------- wipe

    /// <summary>
    /// Removes every Ultra Search summary: the content database, the folder list
    /// and the usage count. The downloaded models stay (they hold nothing of the user's) unless
    /// <paramref name="models"/>.
    /// </summary>
    public void Wipe(bool models)
    {
        CancelScan();
        DropQueryModel();
        _store.Wipe();
        _store = ContentStore.OpenDefault();
        Options.Folders.Clear();
        Options.Paused = false;
        _save();
        if (models)
        {
            ModelStore.Delete();
            UseLight();
        }
        Index = ContentIndex.Empty;
        SetStatus(new UltraStatus(false, 0, 0, null, false, 0, null));
    }

    private void SetStatus(UltraStatus s)
    {
        Status = s;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        CancelScan();
        DropQueryModel();
        _store.Dispose();
    }
}

/// <summary>Full mode needs a one-time download, and with Smart search off the app makes no network call.</summary>
public sealed class SmartSearchOffException() : InvalidOperationException("Smart search is off");
