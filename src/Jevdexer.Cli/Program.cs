using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jevdexer.Discovery;

namespace Jevdexer.Cli;

/// <summary>
/// Developer commands:
///   discover [--out &lt;file&gt;] [--list]   discovery only, to JSON (default `out/entities.json`); local only
///   index [--offline]                    discover, store in the local index, label what changed with Jev
///   search "&lt;query&gt;" [--offline]         query the local index; online, also refines with Jev
///   files ["&lt;query&gt;"]                    crawl files and folders into memory (never to disk), print stats, search
///   folders "&lt;query&gt;" [--offline]        folder Smart search: prints exactly what would be sent; online, Jev's scores
/// </summary>
internal static class Program
{
    private const string Usage = "usage: jevdexer discover [--out <file>] [--list] | index [--offline] | search \"<query>\" [--offline] | files [\"<query>\"] | folders \"<query>\" [--offline]";

    [STAThread]
    private static int Main(string[] args) => args.FirstOrDefault() switch
    {
        "discover" => Discover(args),
        "index" => Commands.Index(args.Contains("--offline")).GetAwaiter().GetResult(),
        "search" when args.Length >= 2 => Commands.Search(args[1], args.Contains("--offline")).GetAwaiter().GetResult(),
        "files" => Commands.Files(args.Length >= 2 ? args[1] : null).GetAwaiter().GetResult(),
        "sandbox-probe" => Commands.SandboxProbe(args.Length >= 2 ? args[1] : null),
        "ultra-test" when args.Length >= 3 => Commands.UltraTest(args[1], args.Skip(2).Where(a => !a.StartsWith("--")).ToList(), args.Contains("--light"), args.Contains("--jev")).GetAwaiter().GetResult(),
        "content" when args.Length >= 2 => Commands.Content(args.Skip(1).Where(a => !a.StartsWith("--")).ToList(), args.Contains("--light")).GetAwaiter().GetResult(),
        "folders" when args.Length >= 2 => Commands.Folders(args[1], args.Contains("--offline")).GetAwaiter().GetResult(),
        _ => Fail(),
    };

    private static int Fail()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int Discover(string[] args)
    {
        var outPath = ArgValue(args, "--out") ?? Path.Combine("out", "entities.json");
        var report = DiscoveryRunner.Run();

        PrintSummary(report);
        if (args.Contains("--list")) PrintList(report.Entities);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(
            new { entities = report.Entities, notLaunchable = report.NotLaunchable, sources = report.Sources }, JsonOptions));
        Console.WriteLine($"\nWrote {outPath}");
        return report.Sources.Concat(report.Enrichers).Any(s => s.Error is not null) ? 1 : 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void PrintSummary(DiscoveryRunner.Report r)
    {
        Console.WriteLine("Source        Sightings   Time");
        foreach (var s in r.Sources)
        {
            var status = s.Error is null ? "" : $"   FAILED: {s.Error}";
            Console.WriteLine($"{s.Source,-13} {s.Count,9}   {s.Elapsed.TotalMilliseconds,6:0} ms{status}");
        }
        Console.WriteLine("\nEnricher       Enriched   Time");
        foreach (var s in r.Enrichers)
        {
            var status = s.Error is null ? "" : $"   FAILED: {s.Error}";
            Console.WriteLine($"{s.Source,-13} {s.Count,9}   {s.Elapsed.TotalMilliseconds,6:0} ms{status}");
        }
        Console.WriteLine($"\nEntities (launchable): {r.Entities.Count}");
        Console.WriteLine($"Not launchable (uninstall entry only): {r.NotLaunchable.Count}");
        Console.WriteLine($"Merge + version info: {r.MergeElapsed.TotalMilliseconds:0} ms");
        Console.WriteLine($"Total time: {r.Elapsed.TotalMilliseconds:0} ms");
    }

    private static void PrintList(IReadOnlyList<Entity> entities)
    {
        Console.WriteLine();
        foreach (var e in entities)
        {
            var tags = e.Enrichment is null ? "" : string.Join(", ", e.Enrichment.Genres.Concat(e.Enrichment.Tags).Take(6));
            Console.WriteLine($"{Trim(e.Name, 40),-40} {Trim(e.Publisher ?? "", 22),-22} {Trim(tags, 60)}");
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
