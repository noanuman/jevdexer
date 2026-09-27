using System.Diagnostics;
using Jevdexer.Discovery.Enrichment;
using Jevdexer.Discovery.Sources;

namespace Jevdexer.Discovery;

/// <summary>Runs every source, merges, then enriches. Failures are isolated per source and per enricher. Call from an STA thread.</summary>
public static class DiscoveryRunner
{
    public sealed record SourceReport(string Source, int Count, TimeSpan Elapsed, string? Error);

    public sealed record Report(
        IReadOnlyList<Entity> Entities,
        IReadOnlyList<Entity> NotLaunchable,
        IReadOnlyList<SourceReport> Sources,
        IReadOnlyList<SourceReport> Enrichers,
        TimeSpan MergeElapsed,
        TimeSpan Elapsed);

    public static IReadOnlyList<IAppSource> DefaultSources() =>
    [
        new AppsFolderSource(),
        new ShortcutsSource(),
        new UninstallKeysSource(),
        new SteamSource(),
        new EpicSource(),
        new GogSource(),
        new RiotSource(),
    ];

    public static IReadOnlyList<IEnricher> DefaultEnrichers() => [new WingetEnricher(), new SteamEnricher()];

    public static Report Run(IReadOnlyList<IAppSource>? sources = null, IReadOnlyList<IEnricher>? enrichers = null)
    {
        var total = Stopwatch.StartNew();
        var raws = new List<RawApp>();
        var reports = new List<SourceReport>();

        foreach (var source in sources ?? DefaultSources())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var found = source.Read().ToList();
                raws.AddRange(found);
                reports.Add(new SourceReport(source.Name, found.Count, sw.Elapsed, null));
            }
            catch (Exception e)
            {
                // One broken source must not cost the others. Reported, never swallowed silently.
                reports.Add(new SourceReport(source.Name, 0, sw.Elapsed, $"{e.GetType().Name}: {e.Message}"));
            }
        }

        var mergeSw = Stopwatch.StartNew();
        var merged = EntityMerger.Merge(raws);
        var mergeElapsed = mergeSw.Elapsed;

        var enrichments = new List<SourceReport>();
        foreach (var enricher in enrichers ?? DefaultEnrichers())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                enrichments.Add(new SourceReport(enricher.Name, enricher.Enrich(merged.Entities), sw.Elapsed, null));
            }
            catch (Exception e)
            {
                // A catalog in an unexpected format costs its enrichment, never the run.
                enrichments.Add(new SourceReport(enricher.Name, 0, sw.Elapsed, $"{e.GetType().Name}: {e.Message}"));
            }
        }

        return new Report(merged.Entities, merged.NotLaunchable, reports, enrichments, mergeElapsed, total.Elapsed);
    }
}
