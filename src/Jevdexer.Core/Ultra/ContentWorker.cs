using Jevdexer.Content.Worker;

namespace Jevdexer.Core.Ultra;

/// <summary>
/// The app's side of the content worker: starts Jevdexer.Worker in its <see cref="Sandbox"/>, hands
/// it the models once, then one file at a time. A file that takes longer than
/// <see cref="FileTimeout"/>, or kills the worker, costs only itself: the worker is restarted and the
/// file is recorded as failed (the scanner does not try it again until it changes).
/// </summary>
public sealed class ContentWorker : IDisposable
{
    public static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(45);

    private readonly string _exe;
    private readonly bool _full;
    private Sandbox? _sandbox;

    /// <param name="full">Load the Full-mode models (they must be downloaded).</param>
    public ContentWorker(bool full, string? exe = null)
    {
        _full = full;
        _exe = exe ?? Path.Combine(AppContext.BaseDirectory, "Jevdexer.Worker.exe");
    }

    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "Jevdexer.Worker.exe"));

    /// <summary>Analyzes one file's bytes. "document" or "photo" by <paramref name="kind"/>.</summary>
    public async Task<ContentResult> AnalyzeAsync(string kind, string extension, byte[] bytes, CancellationToken ct, string? name = null)
    {
        var sandbox = Ensure();
        var call = Task.Run(() =>
        {
            Protocol.Write(sandbox.Input, new WorkerRequest(kind, extension, Name: name), bytes);
            return Protocol.Read<ContentResult>(sandbox.Output)?.Header ?? throw new EndOfStreamException("worker ended");
        }, ct);

        var done = await Task.WhenAny(call, Task.Delay(FileTimeout, ct));
        if (done == call && call.Status == TaskStatus.RanToCompletion) return call.Result;

        Restart(); // a hang, a crash or the memory ceiling: that file is the culprit
        ct.ThrowIfCancellationRequested();
        return new ContentResult { Kind = "failed", Error = done == call ? "worker ended" : "timed out" };
    }

    public BenchmarkResult Benchmark()
    {
        var sandbox = Ensure();
        Protocol.Write(sandbox.Input, new WorkerRequest("benchmark"));
        return Protocol.Read<BenchmarkResult>(sandbox.Output)?.Header ?? throw new EndOfStreamException("worker ended");
    }

    private Sandbox Ensure()
    {
        if (_sandbox is not null) return _sandbox;
        var sandbox = Sandbox.Start(_exe, "serve");
        try
        {
            var parts = new Dictionary<string, int>();
            var payload = new MemoryStream();
            if (_full)
            {
                foreach (var (part, file) in new[] { ("e5", "e5-small-int8.onnx"), ("e5tok", "e5-tokenizer.json"), ("clip", "clip-vision-int8.onnx") })
                {
                    var bytes = ModelStore.Load(file); // hash-checked
                    parts[part] = bytes.Length;
                    payload.Write(bytes);
                }
            }
            Protocol.Write(sandbox.Input, new WorkerRequest("init", Parts: parts), payload.ToArray());
            var ready = Protocol.Read<ContentResult>(sandbox.Output)?.Header;
            if (ready?.Kind != "ready") throw new InvalidOperationException("worker did not start");
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }
        return _sandbox = sandbox;
    }

    private void Restart()
    {
        _sandbox?.Dispose();
        _sandbox = null;
    }

    public void Dispose()
    {
        if (_sandbox is { } s)
        {
            try
            {
                Protocol.Write(s.Input, new WorkerRequest("exit"));
                s.WaitForExit(TimeSpan.FromSeconds(2));
            }
            catch (IOException)
            {
                // already gone
            }
        }
        Restart();
    }
}
