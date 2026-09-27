using Jevdexer.Core.Store;
using Jevdexer.Discovery;
using Jevdexer.Discovery.Enrichment;
using Microsoft.Data.Sqlite;

namespace Jevdexer.Core.Tests;

public class IndexStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"jevdexer-store-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static Entity Entity(string id, string name)
    {
        var e = new Entity { Id = id, Name = name, Publisher = "P", Exe = @"C:\x\a.exe", Enrichment = new EntityEnrichment { SteamType = "Game" } };
        e.Names.Add(name);
        e.Sources.Add("steam");
        e.SourceIds["steam"] = "730";
        e.Enrichment.AddTags(["fps"]);
        return e;
    }

    [Fact]
    public void Round_trips_entities_with_their_lists_and_labels()
    {
        using (var store = new IndexStore(_path))
        {
            store.ReplaceEntities([Entity("a", "Counter-Strike 2")]);
            store.SaveLabels([new AppLabel { EntityId = "a", Model = "m", TagListVersion = "v", InputHash = "h", Background = 0.1,
                Tags = new Dictionary<string, double> { ["game"] = 0.99 }, LabeledAt = DateTimeOffset.UnixEpoch }]);
        }

        using var reopened = new IndexStore(_path);
        var app = Assert.Single(reopened.LoadApps());
        Assert.Equal(["Counter-Strike 2"], app.Entity.Names);
        Assert.Equal("730", app.Entity.SourceIds["steam"]);
        Assert.Equal(["fps"], app.Entity.Enrichment!.Tags);
        Assert.Equal(0.99, app.Label!.Tags["game"]);
    }

    [Fact]
    public void An_uninstalled_app_is_forgotten_with_its_label()
    {
        using var store = new IndexStore(_path);
        store.ReplaceEntities([Entity("a", "A"), Entity("b", "B")]);
        store.SaveLabels([new AppLabel { EntityId = "b", Model = "m", TagListVersion = "v", InputHash = "h", Background = 0,
            Tags = new Dictionary<string, double>(), LabeledAt = DateTimeOffset.UnixEpoch }]);

        store.ReplaceEntities([Entity("a", "A")]);
        var app = Assert.Single(store.LoadApps());
        Assert.Equal("a", app.Entity.Id);
    }

    [Fact]
    public void Tag_list_is_cached_for_offline_use()
    {
        using var store = new IndexStore(_path);
        store.SaveTagList(Fixtures.Tags);
        Assert.Equal(["video games", "play"], store.LoadTagList()!.Tags["game"].Keywords.Skip(2));
    }
}

public class AppDescriberTests
{
    [Fact]
    public void What_leaves_the_machine_never_contains_a_path()
    {
        var e = new Entity { Id = "x", Name = "Secret Game", Exe = @"D:\Clients\Petrovic\game.exe", InstallDir = @"D:\Clients\Petrovic" };
        e.Names.Add("Secret Game");
        var sent = System.Text.Json.JsonSerializer.Serialize(AppDescriber.State(e)) + AppDescriber.Line(e);
        Assert.DoesNotContain("Petrovic", sent);
        Assert.DoesNotContain(@":\", sent);
    }

    [Fact]
    public void Input_hash_changes_with_the_tag_list_version()
    {
        var e = new Entity { Id = "x", Name = "A" };
        Assert.NotEqual(AppDescriber.InputHash(e, "v1"), AppDescriber.InputHash(e, "v2"));
        Assert.Equal(AppDescriber.InputHash(e, "v1"), AppDescriber.InputHash(e, "v1"));
    }
}
