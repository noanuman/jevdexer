using System.Diagnostics;
using Jevdexer.Core;
using Jevdexer.Core.Files;
using Jevdexer.Core.Indexing;
using Jevdexer.Core.Remote;
using Jevdexer.Core.Search;
using Jevdexer.Core.Store;

namespace Jevdexer.Cli;

/// <summary>CLI commands: the same Core the window uses, driven from a terminal.</summary>
internal static class Commands
{
    public static async Task<int> Index(bool offline)
    {
        using var store = IndexStore.OpenDefault();
        using var backend = offline ? null : Jev();
        var result = await new Indexer(store, backend).RunAsync(new Progress<string>(Console.WriteLine));

        Console.WriteLine($"{result.Entities} apps, {result.Labeled} labeled now, {result.Unlabeled} unlabeled, {result.Elapsed.TotalSeconds:0.0} s");
        if (result.LabelError is not null) Console.WriteLine($"labeling skipped: {result.LabelError}");
        return result.LabelError is null || offline ? 0 : 1;
    }

    public static async Task<int> Search(string query, bool offline)
    {
        using var store = IndexStore.OpenDefault();
        using var backend = offline ? null : Jev();
        var engine = new SearchEngine(backend);
        engine.Load(store.LoadApps(), TagCatalog.List);

        var sw = Stopwatch.StartNew();
        var local = engine.Local(query);
        Print($"local ({sw.Elapsed.TotalMilliseconds:0.0} ms)", local);

        if (!engine.NeedsRemote(query, local))
        {
            Console.WriteLine(offline ? "\n(offline)" : "\n(confident name match: no network call)");
            return 0;
        }

        sw.Restart();
        try
        {
            var refined = await engine.RefineAsync(query, CancellationToken.None);
            Print($"refined ({sw.ElapsedMilliseconds} ms)", refined);
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or JevException or TaskCanceledException)
        {
            Console.WriteLine($"\nrefine failed, local results stand: {e.Message}");
            return 1;
        }
    }

    public static async Task<int> Files(string? query)
    {
        using var store = IndexStore.OpenDefault();
        var installDirs = FileRoots.AppFolders(store.LoadApps());
        // JEVDEXER_ROOTS=C:\a;D:\b crawls only those (debugging a root).
        var rules = Environment.GetEnvironmentVariable("JEVDEXER_ROOTS") is { Length: > 0 } only
            ? new FileRoots(only.Split(';'), installDirs)
            : FileRoots.Default(installDirs);
        Console.WriteLine("Roots:");
        foreach (var r in rules.Roots) Console.WriteLine($"  {r}");

        var index = new FileIndex();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        await index.BuildAsync(rules);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"\n{index.FileCount:N0} files, {index.FolderCount:N0} folders in {index.LastBuild.TotalSeconds:0.0} s, ~{(after - before) / 1024 / 1024} MB in memory");

        if (query is null) return 0;
        foreach (var folders in new[] { true, false })
        {
            var sw = Stopwatch.StartNew();
            var hits = FileMatcher.Search(index.Entries, query, folders, 8);
            Console.WriteLine($"\n{(folders ? "Folders" : "Files")} ({sw.Elapsed.TotalMilliseconds:0} ms)");
            foreach (var h in hits) Console.WriteLine($"  {h.Score:0.00}  {h.Entry.Path}");
        }
        return 0;
    }

    /// <summary>Folder Smart search, audited: the shortlist and every summary as sent, then Jev's merged ranking.</summary>
    public static async Task<int> Folders(string query, bool offline)
    {
        using var store = IndexStore.OpenDefault();
        var index = new FileIndex();
        await index.BuildAsync(FileRoots.Default(FileRoots.AppFolders(store.LoadApps())));

        var shortlist = FolderSummaries.Shortlist(index.Entries, query);
        Console.WriteLine($"{shortlist.Count} folders shortlisted locally. Sent as:");
        foreach (var c in shortlist.Take(15)) Console.WriteLine($"  {c.LocalScore,5:0.0}  {c.Summary}");
        if (shortlist.Count > 15) Console.WriteLine($"  ... and {shortlist.Count - 15} more");
        if (offline || shortlist.Count == 0) return 0;

        using var backend = Jev() ?? throw new InvalidOperationException("no AI Gateway key: set it in the app, or JEVDEXER_GATEWAY_KEY / AI_GATEWAY_API_KEY");
        var sw = Stopwatch.StartNew();
        var jev = await FolderSummaries.RefineAsync(backend, index.Entries, query, CancellationToken.None);
        var merged = FolderSummaries.Merge(FileMatcher.Search(index.Entries, query, folders: true, 12), jev);
        Console.WriteLine($"\nRanked ({sw.ElapsedMilliseconds} ms)");
        foreach (var h in merged.Take(10)) Console.WriteLine($"  {h.Score:0.00}  {h.Entry.Path}");
        return 0;
    }

    /// <summary>Starts the content worker in its sandbox and prints what it could reach (all should be blocked).</summary>
    /// <param name="exe">A worker to probe (an installed copy); default: the one next to the CLI.</param>
    public static int SandboxProbe(string? exe)
    {
        Environment.SetEnvironmentVariable("JEVDEXER_PROBE_DIR", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var sandbox = Jevdexer.Core.Ultra.Sandbox.Start(exe ?? Path.Combine(AppContext.BaseDirectory, "Jevdexer.Worker.exe"), "probe");
        using var reader = new StreamReader(sandbox.Output);
        Console.Write(reader.ReadToEnd());
        return sandbox.WaitForExit(TimeSpan.FromSeconds(10)) ? 0 : 1;
    }

    /// <summary>Runs files through the sandboxed content worker and prints what it derived (never the text itself).</summary>
    public static async Task<int> Content(List<string> paths, bool light)
    {
        using var worker = new Jevdexer.Core.Ultra.ContentWorker(full: !light);
        var sw = Stopwatch.StartNew();
        var bench = worker.Benchmark();
        Console.WriteLine($"worker up in {sw.ElapsedMilliseconds} ms; benchmark: {bench.PassageMs} ms/passage, {bench.PhotoMs} ms/photo");
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var kind = Jevdexer.Content.Extract.Imaging.Extensions.Contains(ext) ? "photo" : "document";
            sw.Restart();
            var r = await worker.AnalyzeAsync(kind, ext, await File.ReadAllBytesAsync(path), CancellationToken.None);
            Console.WriteLine($"\n{Path.GetFileName(path)}  [{r.Kind}{(r.Error is null ? "" : ": " + r.Error)}] {sw.ElapsedMilliseconds} ms, {r.Pages} pages, {r.TextLength} chars, ocr {r.UsedOcr}");
            if (r.Title is not null) Console.WriteLine($"  title: {r.Title}");
            Console.WriteLine($"  keywords: {string.Join(", ", r.Keywords.Take(12).Select(k => $"{k.Term}({k.Count})"))}");
            if (r.Tags.Count > 0) Console.WriteLine($"  tags: {string.Join(", ", r.Tags.Select(t => $"{t.Label} {t.Score:0.00}"))}");
            if (r.Sensitive.Count > 0) Console.WriteLine($"  SENSITIVE: {string.Join(", ", r.Sensitive)}");
            Console.WriteLine($"  chunks: {r.Chunks.Count} ({r.Chunks.Count(c => c.Vector is not null)} with vectors)");
        }
        return 0;
    }

    /// <summary>
    /// Ultra Search end to end on one folder, with a throwaway content database (the real profile is
    /// untouched): plan, scan in the sandbox, then each query locally, and with --jev through Jev.
    /// </summary>
    public static async Task<int> UltraTest(string folder, List<string> queries, bool light, bool jev)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"jevdexer-ultra-test-{Guid.NewGuid():N}.db");
        var settings = new Jevdexer.Core.Ultra.UltraSettings { Mode = light ? "light" : "full", PauseOnBattery = false };
        var folders = new List<Jevdexer.Core.Ultra.UltraFolder> { new() { Path = Path.GetFullPath(folder) } };
        using var store = new Jevdexer.Core.Ultra.ContentStore(dbPath);
        try
        {
            var plan = Jevdexer.Core.Ultra.ContentScanner.Plan(folders, store, settings.Mode);
            Console.WriteLine($"plan: {plan.Documents} documents, {plan.Photos} photos ({plan.SampledPhotos} sampled), estimate {plan.Seconds(!light, 150, 230):0} s");
            var sw = Stopwatch.StartNew();
            await Jevdexer.Core.Ultra.ContentScanner.RunAsync(plan, folders, store, () => new Jevdexer.Core.Ultra.ContentWorker(!light), settings,
                new Progress<Jevdexer.Core.Ultra.ContentScanner.Progress>(p => { if (p.Done % 10 == 0) Console.Write($"\r  {p.Done}/{p.Total}   "); }), CancellationToken.None);
            var index = new Jevdexer.Core.Ultra.ContentIndex(store.LoadAll(), folders);
            Console.WriteLine($"\rscanned in {sw.Elapsed.TotalSeconds:0} s: {index.Documents} documents, {index.Photos} photos, {index.Failed} failed; sensitive: " +
                              string.Join("; ", index.SensitiveRoots.Select(kv => string.Join(", ", kv.Value))));

            using var embedder = light ? null : new Jevdexer.Content.Models.TextEmbedder(Jevdexer.Core.Ultra.ModelStore.Load("e5-small-int8.onnx"), Jevdexer.Core.Ultra.ModelStore.Load("e5-tokenizer.json"));
            using var backend = jev ? Jev() : null;
            using var clip = light ? null : new Jevdexer.Content.Models.ClipText(Jevdexer.Core.Ultra.ModelStore.Load("clip-text-int8.onnx"), Jevdexer.Core.Ultra.ModelStore.Load("clip-tokenizer.json"));
            var neutral = clip?.Embed("a photo");
            foreach (var q in queries)
            {
                sw.Restart();
                var photo = clip is null ? null : new Jevdexer.Core.Ultra.ClipQuery(clip.Embed(q), neutral!);
                var hits = index.Search(q, embedder?.EmbedQuery(q), photo: photo);
                Console.WriteLine($"\n\"{q}\" local ({sw.ElapsedMilliseconds} ms)");
                foreach (var h in hits.Take(6)) Console.WriteLine($"  {h.Score:0.00} {(h.IsFolder ? "[folder] " : "")}{Path.GetFileName(h.Path)}{(h.Private ? " (private)" : "")}  {h.Why}");
                if (Environment.GetEnvironmentVariable("JEVDEXER_EXPLAIN") == "1")
                    foreach (var (p, cos, m, k) in index.Explain(q, embedder?.EmbedQuery(q)).Take(10))
                        Console.WriteLine($"     cos {cos:0.000} meaning {m:0.00} keyword {k:0.00}  {Path.GetFileName(p)}");
                if (backend is null) continue;
                var candidates = hits.Concat(index.Candidates(q, embedder?.EmbedQuery(q))).DistinctBy(h => h.Path).Take(30).ToList();
                var sendable = candidates.Select(h => (h, s: index.Summary(h))).Where(x => x.s is not null).ToList();
                Console.WriteLine($"  sent to Jev ({sendable.Count}), e.g.: {sendable.FirstOrDefault().s}");
                sw.Restart();
                var r = await backend.SearchContentAsync(q, sendable.Select((x, i) => new JevClient.FolderRequest($"c{i}", x.s!)).ToList());
                var merged = Jevdexer.Core.Ultra.ContentIndex.Merge(candidates, sendable.Select((x, i) => (x.h.Path, p: r.Items.GetValueOrDefault($"c{i}"))).ToDictionary(x => x.Path, x => x.p));
                Console.WriteLine($"  with Jev ({sw.ElapsedMilliseconds} ms):");
                foreach (var h in merged.Take(6)) Console.WriteLine($"  {h.Score:0.00} {(h.IsFolder ? "[folder] " : "")}{Path.GetFileName(h.Path)}");
            }
        }
        finally
        {
            store.Wipe();
        }
        return 0;
    }

    /// <summary>Jev with the app's key (settings.json), or AI_GATEWAY_API_KEY for development; null without either.</summary>
    private static JevClient? Jev()
    {
        var settings = Settings.Load();
        var key = settings.GatewayKey.Length > 0 ? settings.GatewayKey : Environment.GetEnvironmentVariable("AI_GATEWAY_API_KEY") ?? "";
        return key.Length > 0 ? new JevClient(key) : null;
    }

    private static void Print(string title, List<SearchResult> results)
    {
        Console.WriteLine($"\n{title}");
        if (results.Count == 0) Console.WriteLine("  (nothing)");
        foreach (var r in results)
            Console.WriteLine($"  {r.Score:0.00}  {r.App.Entity.Name,-40} local {r.Local:0.00}  tags {r.Tags:0.00}  jev {(r.Jev is { } j ? j.ToString("0.00") : "-")}");
    }
}
