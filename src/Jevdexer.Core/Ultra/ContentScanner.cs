using System.Runtime.InteropServices;
using Jevdexer.Content.Extract;
using Jevdexer.Content.Worker;
using Jevdexer.Core.Files;

namespace Jevdexer.Core.Ultra;

/// <summary>One file a scan will look at.</summary>
public sealed record ScanItem(string Path, string Root, string Kind, long Size, DateTime Modified, bool Unchanged);

/// <summary>What a scan will cost, shown before it starts.</summary>
public sealed record ScanPlan(IReadOnlyList<ScanItem> Items)
{
    /// <summary>The mode the files will be analyzed in ("light" or "full"; Full falls back to Light without models).</summary>
    public string Mode { get; init; } = "light";

    public int Documents => Items.Count(i => i.Kind == "document");
    public int Photos => Items.Count(i => i.Kind is "photo" or "photo-unsampled");
    public int SampledPhotos => Items.Count(i => i.Kind == "photo");
    public int ToDo => Items.Count(i => !i.Unchanged);

    /// <summary>Seconds of background work: per-document extraction plus chunks, per sampled photo.</summary>
    public double Seconds(bool full, double passageMs, double photoMs)
    {
        var docs = Items.Count(i => i.Kind == "document" && !i.Unchanged);
        var photos = Items.Count(i => i.Kind == "photo" && !i.Unchanged);
        var perDoc = 0.25 + (full ? 3 * passageMs / 1000 : 0); // extraction, plus ~3 chunks on average
        var perPhoto = full ? photoMs / 1000 + 0.05 : 0.15;     // Light: only screenshots get OCR
        return docs * perDoc + photos * perPhoto;
    }
}

/// <summary>
/// Ultra Search's scan of the folders the user picked. Walks each folder (skipping the same
/// system, tool and hidden folders as the file index), plans the work, then reads each file and
/// hands its bytes to the sandboxed <see cref="ContentWorker"/>. Incremental: an unchanged file
/// (same size and time) is skipped. Photos are sampled: at most <see cref="PhotosPerFolder"/> per
/// folder are analyzed, spread over the folder; the others count for the folder's summary.
/// Polite: waits while the user is typing or moving the mouse, and while on battery.
/// </summary>
public static class ContentScanner
{
    /// <summary>
    /// Bumped when the worker learns something new about files, so a scan re-reads everything once.
    /// Stored with the mode: "full.3".
    /// </summary>
    public const int AnalysisVersion = 3;

    /// <summary>What a file analyzed now is stamped with.</summary>
    public static string Stamp(string mode) => $"{mode}.{AnalysisVersion}";

    /// <summary>
    /// Photos analyzed per folder: all of them up to this many, a spread of this many beyond it.
    /// A description can only find a photo that was looked at.
    /// </summary>
    public const int PhotosPerFolder = 3000;
    private const long MaxDocumentBytes = 100L * 1024 * 1024;
    private const long MaxPhotoBytes = 40L * 1024 * 1024;
    private const int MaxFilesPerRoot = 200_000;

    public static ScanPlan Plan(IEnumerable<UltraFolder> folders, ContentStore store, string mode)
    {
        var items = new List<ScanItem>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Path)) continue;
            foreach (var dir in Walk(folder.Path))
            {
                FileInfo[] files;
                try
                {
                    files = new DirectoryInfo(dir).GetFiles();
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                var photos = new List<FileInfo>();
                foreach (var f in files)
                {
                    if (f.Attributes.HasFlag(FileAttributes.Hidden) || f.Attributes.HasFlag(FileAttributes.System) || f.Name.StartsWith("~$")) continue;
                    if (IsCloudOnly(f.Attributes)) continue; // reading it would download it (OneDrive "online-only")
                    var ext = f.Extension.TrimStart('.').ToLowerInvariant();
                    if (DocumentText.Extensions.Contains(ext) && f.Length <= MaxDocumentBytes && f.Length > 0)
                        items.Add(Item(f, folder.Path, "document", store, mode));
                    else if (Imaging.Extensions.Contains(ext) && f.Length <= MaxPhotoBytes && f.Length > 0)
                        photos.Add(f);
                }

                var sampled = DocumentText.Spread([.. Enumerable.Range(0, photos.Count)], PhotosPerFolder).ToHashSet();
                var ordered = photos.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    items.Add(Item(ordered[i], folder.Path, sampled.Contains(i) ? "photo" : "photo-unsampled", store, mode));
                if (items.Count > MaxFilesPerRoot) break;
            }
        }
        return new ScanPlan(items) { Mode = mode };
    }

    private static ScanItem Item(FileInfo f, string root, string kind, ContentStore store, string mode)
    {
        var modified = f.LastWriteTimeUtc;
        // Unsampled photos are not analyzed, so the mode does not matter for them.
        var unchanged = store.Stamp(f.FullName) is { } s && s.Size == f.Length && s.Modified == modified
                        && (s.Mode == Stamp(mode) || kind == "photo-unsampled");
        return new ScanItem(f.FullName, root, kind, f.Length, modified, unchanged);
    }

    /// <summary>The folder and its subfolders, minus what the file index never enters.</summary>
    private static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            DirectoryInfo[] subs;
            try
            {
                subs = new DirectoryInfo(dir).GetDirectories();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var s in subs)
            {
                if (s.Attributes.HasFlag(FileAttributes.ReparsePoint) || s.Attributes.HasFlag(FileAttributes.Hidden) || s.Attributes.HasFlag(FileAttributes.System)) continue;
                if (FileRoots.SkippedNames.Contains(s.Name) || s.Name.StartsWith('.') || s.Name.StartsWith('$')) continue;
                stack.Push(s.FullName);
            }
        }
    }

    /// <summary>
    /// OneDrive (and other cloud) placeholders: "online-only" files whose bytes are not on the PC.
    /// Opening one downloads it, so a scan of Pictures could pull gigabytes; they are skipped
    /// until the user makes them available offline.
    /// </summary>
    public static bool IsCloudOnly(FileAttributes a) =>
        ((int)a & (RecallOnDataAccess | RecallOnOpen)) != 0 || a.HasFlag(FileAttributes.Offline);

    private const int RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;

    public sealed record Progress(int Done, int Total, string? Folder);

    /// <summary>
    /// Runs the plan: every changed item is analyzed and saved; files gone since are forgotten.
    /// Returns when done or cancelled; a later run continues where this one stopped.
    /// </summary>
    public static async Task RunAsync(ScanPlan plan, IEnumerable<UltraFolder> folders, ContentStore store, Func<ContentWorker> worker,
        UltraSettings settings, IProgress<Progress>? progress, CancellationToken ct)
    {
        foreach (var folder in folders)
            store.RemoveMissing(folder.Path, plan.Items.Where(i => i.Root == folder.Path).Select(i => ContentStore.Key(i.Path)).ToHashSet());

        var work = plan.Items.Where(i => !i.Unchanged).ToList();
        if (work.Count == 0)
        {
            progress?.Report(new Progress(0, 0, null));
            return;
        }

        using var w = worker();
        for (var n = 0; n < work.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            await WaitUntilPolite(settings, ct);
            var item = work[n];
            progress?.Report(new Progress(n, work.Count, Path.GetFileName(Path.GetDirectoryName(item.Path))));

            ContentResult? result = null;
            var kind = item.Kind;
            if (kind is "document" or "photo")
            {
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(item.Path, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue; // locked or gone: try again next scan
                }
                result = await w.AnalyzeAsync(kind, Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(), bytes, ct,
                    kind == "document" ? Path.GetFileNameWithoutExtension(item.Path) : null);
                if (result.Kind == "failed") kind = "failed";
            }
            store.Save(new ContentRecord { Path = item.Path, Root = item.Root, Size = item.Size, Modified = item.Modified, Kind = kind, Mode = Stamp(plan.Mode), Result = result });
        }
        progress?.Report(new Progress(work.Count, work.Count, null));
    }

    // ---------------------------------------------------------------- politeness

    private static async Task WaitUntilPolite(UltraSettings settings, CancellationToken ct)
    {
        while (true)
        {
            if (settings.Paused) throw new OperationCanceledException("paused");
            if (settings.PauseOnBattery && OnBattery()) await Task.Delay(TimeSpan.FromSeconds(30), ct);
            else if (IdleFor() < TimeSpan.FromSeconds(2)) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            else return;
        }
    }

    /// <summary>Time since the last keyboard or mouse input anywhere in the session.</summary>
    public static TimeSpan IdleFor()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.MaxValue;
    }

    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
