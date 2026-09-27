using System.Text;
using Jevdexer.Discovery.Enrichment;
using Microsoft.Data.Sqlite;

namespace Jevdexer.Discovery.Tests;

public class SteamAppInfoTests
{
    /// <summary>Builds a minimal format-29 appinfo.vdf: one app whose common section holds type, genres and store_tags.</summary>
    private static byte[] BuildV29(uint appId, string type, int[] genres, int[] tags)
    {
        string[] strings = ["appinfo", "common", "type", "genres", "store_tags", "0", "1", "2"];
        int S(string s) => Array.IndexOf(strings, s);

        var kv = new MemoryStream();
        var w = new BinaryWriter(kv);
        void Key(byte t, string k) { w.Write(t); w.Write(S(k)); }
        void IntList(string name, int[] values)
        {
            Key(0, name);
            for (var i = 0; i < values.Length; i++) { Key(2, i.ToString()); w.Write(values[i]); }
            w.Write((byte)8);
        }
        Key(0, "appinfo");
        Key(0, "common");
        Key(1, "type"); w.Write(Encoding.UTF8.GetBytes(type)); w.Write((byte)0);
        IntList("genres", genres);
        IntList("store_tags", tags);
        w.Write((byte)8); w.Write((byte)8); w.Write((byte)8); // common, appinfo, root

        var body = kv.ToArray();
        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write(0x07564429u); f.Write(1u); f.Write(0L); // magic, universe, string table offset (patched below)
        f.Write(appId); f.Write(4 + 4 + 8 + 20 + 4 + 20 + body.Length);
        f.Write(new byte[4 + 4 + 8 + 20 + 4 + 20]);
        f.Write(body);
        f.Write(0u); // end of apps
        var tableAt = file.Position;
        f.Write(strings.Length);
        foreach (var s in strings) { f.Write(Encoding.UTF8.GetBytes(s)); f.Write((byte)0); }
        var bytes = file.ToArray();
        BitConverter.GetBytes(tableAt).CopyTo(bytes, 8);
        return bytes;
    }

    [Fact]
    public void Reads_type_genres_and_tags_from_format_29()
    {
        var data = BuildV29(730, "Game", [1, 37], [1663, 1774]);
        var common = SteamAppInfo.Read(data, new HashSet<uint> { 730 })[730];

        Assert.Equal("Game", common.Type);
        Assert.Equal([1, 37], common.Genres);
        Assert.Equal("FPS", SteamTags.Tag(common.StoreTags[0]));
        Assert.Equal("Free To Play", SteamTags.Genre(common.Genres[1]));
    }

    [Fact]
    public void Skips_apps_not_asked_for() =>
        Assert.Empty(SteamAppInfo.Read(BuildV29(730, "Game", [1], [19]), new HashSet<uint> { 440 }));
}

public class WingetEnricherTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"jevdexer-winget-{Guid.NewGuid():N}.db");

    public WingetEnricherTests()
    {
        using var c = new SqliteConnection($"Data Source={_db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            create table packages(rowid integer primary key, id text, name text, hash blob);
            create table tags2(rowid integer primary key, tag text);
            create table tags2_map(tag int, package int);
            create table productcodes2(productcode text, package int);
            create table pfns2(pfn text, package int);
            create table norm_names2(norm_name text, package int);
            create table norm_publishers2(norm_publisher text, package int);
            insert into packages(rowid, id, name) values (1,'Google.Chrome.Beta','Google Chrome Beta'), (2,'Google.Chrome','Google Chrome'),
                                        (3,'Someone.Notepad','Notepad'), (4,'ByteDance.CapCut','CapCut');
            insert into tags2 values (1,'browser'), (2,'video-editor'), (3,'浏览器'), (4,'text');
            insert into tags2_map values (1,1),(3,1),(1,2),(3,2),(4,3),(2,4);
            insert into productcodes2 values ('google chrome',1), ('google chrome',2);
            insert into norm_names2 values ('capcut',4);
            insert into norm_publishers2 values ('google llc',1),('google llc',2),('someone',3),('bytedance',4);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private static Entity E(string name, string? publisher, string? uninstallKey = null)
    {
        var e = new Entity { Id = name, Name = name, Publisher = publisher };
        e.Names.Add(name);
        if (uninstallKey is not null) e.SourceIds["uninstall"] = uninstallKey;
        return e;
    }

    [Fact]
    public void Prefers_the_stable_package_over_a_pre_release_sharing_the_product_code()
    {
        var chrome = E("Google Chrome", "Google LLC");
        new WingetEnricher(_db).Enrich([chrome]);

        Assert.Equal("Google.Chrome", chrome.Enrichment!.WingetId);
        Assert.Equal(["browser"], chrome.Enrichment.Tags); // the Chinese duplicate is dropped
    }

    [Fact]
    public void A_bare_name_match_without_an_agreeing_publisher_is_ignored()
    {
        var notepad = E("Notepad", "Microsoft Corporation");
        Assert.Equal(0, new WingetEnricher(_db).Enrich([notepad]));
        Assert.Null(notepad.Enrichment);
    }

    [Fact]
    public void Name_plus_publisher_matches()
    {
        var capcut = E("CapCut", "Bytedance Pte. Ltd.");
        new WingetEnricher(_db).Enrich([capcut]);
        Assert.Equal("video-editor", Assert.Single(capcut.Enrichment!.Tags));
    }
}

public class TagFilterTests
{
    [Theory]
    [InlineData("video-editor", true)]
    [InlineData("free to play", true)]
    [InlineData("浏览器", false)]
    [InlineData("9nztwsqntd0s", false)]
    [InlineData("screen-recording", true)]
    public void Keeps_meaningful_tags(string tag, bool kept) => Assert.Equal(kept, EntityEnrichment.IsUseful(tag));
}
