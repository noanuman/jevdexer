using Jevdexer.Core.Search;
using Jevdexer.Core.Remote;
using Jevdexer.Core.Store;
using Jevdexer.Discovery;

namespace Jevdexer.Core.Indexing;

/// <summary>
/// discover -> store -> label what changed. Discovery runs on its own STA thread (the AppsFolder
/// needs one). Labeling needs Jev; without it the index still updates and search still works
/// from names, and unlabeled apps are labeled on a later run.
/// </summary>
public sealed class Indexer(IndexStore store, JevClient? backend)
{
    private const int LabelBatch = 25;

    public sealed record Result(int Entities, int Labeled, int Unlabeled, string? LabelError, TimeSpan Elapsed);

    public async Task<Result> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report("Finding apps...");
        var report = await RunOnStaThread(() => DiscoveryRunner.Run());
        if (backend is not null)
        {
            // Smart search is on: public catalog descriptions are allowed (winget CDN, public ids only).
            progress?.Report("Reading app descriptions...");
            using var http = new HttpClient();
            await new Catalog.WingetCatalog(http, store).DescribeAsync(report.Entities, ct);
        }
        store.ReplaceEntities(report.Entities);

        if (backend is null) return new Result(report.Entities.Count, 0, report.Entities.Count, "no backend", sw.Elapsed);

        try
        {
            var tags = TagCatalog.List;
            store.SaveTagList(tags);

            var stale = store.LoadApps()
                .Where(a => a.Label is null || a.Label.InputHash != AppDescriber.InputHash(a.Entity, tags.TagListVersion))
                .Select(a => a.Entity)
                .ToList();

            var labeled = 0;
            foreach (var batch in stale.Chunk(LabelBatch))
            {
                progress?.Report($"Understanding apps... {labeled}/{stale.Count}");
                var response = await backend.LabelAsync(
                    batch.Select(e => new JevClient.LabelRequestApp(e.Id, AppDescriber.State(e))).ToList(), ct);
                var byId = batch.ToDictionary(e => e.Id);
                store.SaveLabels(response.Labels.Select(l => new AppLabel
                {
                    EntityId = l.Id,
                    Model = response.Model,
                    TagListVersion = response.TagListVersion,
                    InputHash = AppDescriber.InputHash(byId[l.Id], response.TagListVersion),
                    Background = l.Background,
                    Tags = l.Tags,
                    LabeledAt = DateTimeOffset.UtcNow,
                }));
                labeled += batch.Length;
            }
            return new Result(report.Entities.Count, labeled, 0, null, sw.Elapsed);
        }
        catch (Exception e) when (e is HttpRequestException or JevException or TaskCanceledException)
        {
            var unlabeled = store.LoadApps().Count(a => a.Label is null);
            return new Result(report.Entities.Count, 0, unlabeled, e.Message, sw.Elapsed);
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        }) { IsBackground = true, Name = "discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
