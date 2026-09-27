using System.Net.Sockets;
using Jevdexer.Content.Analysis;
using Jevdexer.Content.Models;
using Jevdexer.Content.Worker;

namespace Jevdexer.Worker;

/// <summary>
/// The content worker. Started by the app inside an AppContainer with no capabilities (Core,
/// Ultra/Sandbox.cs): it cannot reach the network or open the user's files. It reads requests
/// from stdin and answers on stdout (<see cref="Protocol"/>): models once (init), then one file's
/// bytes per request. A file that crashes or hangs it is the app's to skip; the job object kills
/// it if memory runs away.
///
/// `probe` checks the sandbox from the inside: every line must say "blocked".
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "probe") return Probe();
        if (args.FirstOrDefault() != "serve")
        {
            Console.Error.WriteLine("usage: Jevdexer.Worker serve | probe");
            return 2;
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Analyzer? analyzer = null;
        try
        {
            while (Protocol.Read<WorkerRequest>(input) is var (request, payload))
            {
                switch (request.Op)
                {
                    case "init":
                        analyzer?.Dispose();
                        analyzer = Init(request, payload);
                        Protocol.Write(output, new ContentResult { Kind = "ready" });
                        break;
                    case "document" or "photo":
                        Protocol.Write(output, await Analyze(analyzer, request, payload));
                        break;
                    case "benchmark":
                        Protocol.Write(output, analyzer is null ? new BenchmarkResult(0, 0, false, false, "not initialized") : await analyzer.BenchmarkAsync());
                        break;
                    case "exit":
                        return 0;
                    default:
                        Protocol.Write(output, new ContentResult { Kind = "failed", Error = "unknown op" });
                        break;
                }
            }
            return 0;
        }
        finally
        {
            analyzer?.Dispose();
        }
    }

    private static Analyzer Init(WorkerRequest request, byte[] payload)
    {
        var parts = new Dictionary<string, byte[]>();
        var at = 0;
        foreach (var (name, length) in request.Parts ?? [])
        {
            parts[name] = payload.AsSpan(at, length).ToArray();
            at += length;
        }
        var text = parts.TryGetValue("e5", out var e5) && parts.TryGetValue("e5tok", out var tok) ? new TextEmbedder(e5, tok) : null;
        var photos = parts.TryGetValue("clip", out var clip) ? new PhotoTagger(clip) : null;
        return new Analyzer(text, photos);
    }

    private static async Task<ContentResult> Analyze(Analyzer? analyzer, WorkerRequest request, byte[] bytes)
    {
        if (analyzer is null) return new ContentResult { Kind = "failed", Error = "not initialized" };
        try
        {
            var ext = request.Extension ?? "";
            return request.Op == "photo" ? await analyzer.PhotoAsync(bytes, ext) : await analyzer.DocumentAsync(bytes, ext, request.Name);
        }
        catch (Exception e)
        {
            // Damaged or hostile files end here. The type only: messages can quote content.
            return new ContentResult { Kind = "failed", Error = e.GetType().Name };
        }
    }

    private static int Probe()
    {
        Check("network", () =>
        {
            using var tcp = new TcpClient();
            tcp.ConnectAsync("1.1.1.1", 443).Wait(TimeSpan.FromSeconds(3));
            if (!tcp.Connected) throw new IOException("not connected");
        });
        var profile = Environment.GetEnvironmentVariable("JEVDEXER_PROBE_DIR") ?? "";
        Check("user files", () => Directory.GetFiles(profile));
        return 0;
    }

    private static void Check(string what, Action attempt)
    {
        try
        {
            attempt();
            Console.WriteLine($"{what}: ALLOWED");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{what}: blocked ({e.GetType().Name})");
        }
    }
}
