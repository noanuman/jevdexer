using System.Security.Cryptography;

namespace Jevdexer.Core.Ultra;

/// <summary>
/// The Full-mode models, downloaded once from Hugging Face when the user turns Full mode on, into
/// `%LOCALAPPDATA%\Jevdexer\models`. Every file is pinned by SHA-256: a file that does not match is
/// deleted and never loaded. Public files only; the request carries nothing about the user.
/// </summary>
public static class ModelStore
{
    public sealed record ModelFile(string Name, string Url, string Sha256, long Size);

    public static readonly IReadOnlyList<ModelFile> Files =
    [
        new("e5-small-int8.onnx", "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model_quantized.onnx",
            "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193", 118_308_185),
        new("e5-tokenizer.json", "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/tokenizer.json",
            "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39", 17_082_730),
        new("clip-vision-int8.onnx", "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx",
            "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299", 89_117_001),
        // CLIP's text half: the user's words compared with each photo directly ("a woman in a checked blazer").
        new("clip-text-int8.onnx", "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/text_model_quantized.onnx",
            "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a", 64_504_507),
        new("clip-tokenizer.json", "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/tokenizer.json",
            "f7f3b7af117d467b58374797691a6438d3e6b9e9cef800dfd5dced7f697a90cd", 2_224_119),
    ];

    public static long TotalBytes => Files.Sum(f => f.Size);

    public static string Dir => Path.Combine(Settings.DataDir, "models");

    public static string PathOf(string name) => Path.Combine(Dir, name);

    /// <summary>All files present with the right size (the hash is checked when downloaded and when loaded).</summary>
    public static bool Present => Files.All(f => new FileInfo(PathOf(f.Name)) is { Exists: true } fi && fi.Length == f.Size);

    /// <summary>Downloads what is missing. Progress: bytes done of <see cref="TotalBytes"/>.</summary>
    public static async Task DownloadAsync(IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        long done = 0;
        foreach (var f in Files)
        {
            var target = PathOf(f.Name);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == f.Size && Verify(target, f))
            {
                done += f.Size;
                progress?.Report(done);
                continue;
            }

            var partial = target + ".part";
            using (var res = await http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                await using var input = await res.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(partial);
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report(done);
                }
            }
            if (!Verify(partial, f))
            {
                File.Delete(partial);
                throw new InvalidDataException($"{f.Name} did not match its pinned hash");
            }
            File.Move(partial, target, overwrite: true);
        }
    }

    /// <summary>Reads a model, checking its hash: a swapped file is refused.</summary>
    public static byte[] Load(string name)
    {
        var f = Files.First(x => x.Name == name);
        var bytes = File.ReadAllBytes(PathOf(name));
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(f.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{name} does not match its pinned hash");
        return bytes;
    }

    public static void Delete()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }

    private static bool Verify(string path, ModelFile f)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(f.Sha256, StringComparison.Ordinal);
    }
}
