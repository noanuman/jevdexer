using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Jevdexer.Core.Store;
using Jevdexer.Discovery;

namespace Jevdexer.Core.Catalog;

/// <summary>
/// App descriptions from winget's public CDN, the same requests winget.exe makes:
///   1. `/cache/packages/&lt;id&gt;/&lt;hash8&gt;/versionData.mszyml`: the version list (MSZIP-compressed YAML),
///      newest first, each with its manifest path (`rP`).
///   2. `/cache/&lt;rP&gt;`: the manifest (plain YAML) with `ShortDescription`.
/// Only public package ids are sent. Runs only with Smart search on. Results are cached
/// (DPAPI-protected, in the index) under id + hash8, so a package is fetched again only when winget
/// publishes a new version of it. Failures are silent: descriptions are a bonus, never required.
/// </summary>
public sealed partial class WingetCatalog(HttpClient http, IndexStore store)
{
    public const string Cdn = "https://cdn.winget.microsoft.com/cache/";

    /// <summary>Fills <c>Enrichment.Description</c> for winget-matched entities. Returns how many have one.</summary>
    public async Task<int> DescribeAsync(IReadOnlyList<Entity> entities, CancellationToken ct = default)
    {
        var cache = store.LoadCatalog("winget");
        var targets = entities.Where(e => e.Enrichment is { WingetId: not null, WingetHash: not null }).ToList();
        var changed = false;

        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (e, token) =>
        {
            var key = $"{e.Enrichment!.WingetId}|{e.Enrichment.WingetHash}";
            string? description;
            lock (cache) cache.TryGetValue(key, out description);
            if (description is null)
            {
                description = await FetchAsync(e.Enrichment.WingetId!, e.Enrichment.WingetHash!, token) ?? "";
                lock (cache)
                {
                    cache[key] = description;
                    changed = true;
                }
            }
            if (description.Length > 0) e.Enrichment.Description = description;
        });

        if (changed) store.SaveCatalog("winget", cache);
        return targets.Count(e => e.Enrichment!.Description is not null);
    }

    private async Task<string?> FetchAsync(string id, string hash8, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var versions = await http.GetByteArrayAsync($"{Cdn}packages/{Uri.EscapeDataString(id)}/{hash8}/versionData.mszyml", cts.Token);
            if (LatestManifestPath(Mszip.FirstBlock(versions)) is not { } path) return null;
            var manifest = await http.GetStringAsync(Cdn + path, cts.Token);
            return ShortDescription(manifest);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The first `rP:` in the version list: the newest version's manifest path.</summary>
    internal static string? LatestManifestPath(string versionData)
    {
        var m = ManifestPath().Match(versionData);
        return m.Success && !m.Groups[1].Value.Contains("..") ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>`ShortDescription:` from a manifest, unquoted, one line, at most 200 characters.</summary>
    internal static string? ShortDescription(string manifest)
    {
        var m = ShortDescriptionLine().Match(manifest);
        if (!m.Success) return null;
        var value = m.Groups[1].Value.Trim().Trim('"', '\'').Trim();
        if (value.Length == 0 || value is ">-" or "|" or ">") return null; // block scalars: not worth a YAML parser
        return value.Length <= 200 ? value : value[..197] + "...";
    }

    [GeneratedRegex(@"^\s*-?\s*rP:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex ManifestPath();

    [GeneratedRegex(@"^ShortDescription:[ \t]*(.*)$", RegexOptions.Multiline)]
    private static partial Regex ShortDescriptionLine();
}

/// <summary>
/// Windows Compression API MSZIP container: a header, then blocks each starting with "CK" and raw
/// deflate. Later blocks may reference earlier output as a dictionary, which .NET's DeflateStream
/// cannot take, so only the first block (up to 32 KB of text) is decoded. For a version list, that
/// always holds the newest versions.
/// </summary>
internal static class Mszip
{
    public static string FirstBlock(byte[] data)
    {
        var start = data.AsSpan().IndexOf("CK"u8);
        if (start < 0) throw new InvalidDataException("not an MSZIP container");
        using var input = new MemoryStream(data, start + 2, data.Length - start - 2);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        try
        {
            deflate.CopyTo(output);
        }
        catch (InvalidDataException) when (output.Length > 0)
        {
            // Ran into the next block's "CK": everything decoded so far is the first block.
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
