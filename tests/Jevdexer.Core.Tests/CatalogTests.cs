using System.IO.Compression;
using System.Text;
using Jevdexer.Core.Catalog;
using Jevdexer.Discovery;

namespace Jevdexer.Core.Tests;

public class WingetCatalogTests
{
    /// <summary>An MSZIP container as winget's CDN serves it: a header, then "CK" and raw deflate.</summary>
    private static byte[] Mszipped(string text)
    {
        using var body = new MemoryStream();
        using (var deflate = new DeflateStream(body, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(Encoding.UTF8.GetBytes(text));
        return [0x0A, 0x51, 0xE5, 0xC0, 0x18, 0x00, 0x7F, 0x02, .. new byte[20], .. "CK"u8, .. body.ToArray()];
    }

    private const string VersionData = """
        sV: 1.0
        vD:
        - v: 9.4.0.4015
          rP: manifests/b/ByteDance/CapCut/9.4.0.4015/2490
          s256H: 3b15
        - v: 9.2.0.3931
          rP: manifests/b/ByteDance/CapCut/9.2.0.3931/f962
        """;

    [Fact]
    public void Decodes_the_first_mszip_block_and_takes_the_newest_manifest()
    {
        var text = Mszip.FirstBlock(Mszipped(VersionData));
        Assert.Equal("manifests/b/ByteDance/CapCut/9.4.0.4015/2490", WingetCatalog.LatestManifestPath(text));
    }

    [Theory]
    [InlineData("ShortDescription: A video editing tool packed with features\r\nTags:", "A video editing tool packed with features")]
    [InlineData("ShortDescription: \"Quoted: with a colon\"\n", "Quoted: with a colon")]
    [InlineData("ShortDescription: >-\n  folded text\n", null)]
    [InlineData("Description: only the long one\n", null)]
    public void Reads_the_short_description(string manifest, string? expected) =>
        Assert.Equal(expected, WingetCatalog.ShortDescription(manifest));

    [Fact]
    public void A_manifest_path_cannot_climb_out_of_the_cache() =>
        Assert.Null(WingetCatalog.LatestManifestPath("- rP: ../../etc/passwd"));

    [Fact]
    public void Adding_a_description_changes_only_that_apps_input_hash()
    {
        var plain = new Entity { Id = "a", Name = "Notepad" };
        var before = AppDescriber.InputHash(plain, "v");
        plain.Enrichment = new Discovery.Enrichment.EntityEnrichment(); // enrichment without a description
        Assert.Equal(before, AppDescriber.InputHash(plain, "v"));

        plain.Enrichment.Description = "A text editor";
        Assert.NotEqual(before, AppDescriber.InputHash(plain, "v"));
    }
}
