using System.Text;
using Jevdexer.Core.History;
using Jevdexer.Core.Store;
using Jevdexer.Discovery;
using Microsoft.Data.Sqlite;

namespace Jevdexer.Core.Tests;

/// <summary>Nothing the app stores is readable as plain text in the file.</summary>
public class ProtectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jevdexer-protect-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static bool FileContains(string path, string text) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(text, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Round_trips_and_passes_legacy_plain_text_through()
    {
        var protectedText = Protector.Protect("secret token");
        Assert.StartsWith("dpapi:", protectedText);
        Assert.Equal("secret token", Protector.Unprotect(protectedText));
        Assert.Equal("plain", Protector.Unprotect("plain"));
    }

    [Fact]
    public void Index_rows_are_not_readable_in_the_database_file()
    {
        var path = Path.Combine(_dir, "index.db");
        using (var store = new IndexStore(path))
        {
            var e = new Entity { Id = "a", Name = "Petrovic Accounting Suite", Publisher = "Petrovic d.o.o." };
            e.Names.Add(e.Name);
            store.ReplaceEntities([e]);
            store.SaveLabels([new AppLabel { EntityId = "a", Model = "m", TagListVersion = "v", InputHash = "h", Background = 0,
                Tags = new Dictionary<string, double> { ["spreadsheets"] = 0.97 }, LabeledAt = DateTimeOffset.UnixEpoch }]);
            Assert.Equal("Petrovic Accounting Suite", Assert.Single(store.LoadApps()).Entity.Name);
        }
        SqliteConnection.ClearAllPools();
        Assert.False(FileContains(path, "Petrovic"));
        Assert.False(FileContains(path, "spreadsheets"));
    }

    [Fact]
    public void Legacy_plain_rows_are_encrypted_on_open_and_leave_no_trace()
    {
        var path = Path.Combine(_dir, "legacy.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                create table entities(id text primary key, json text not null, seen_at text not null);
                insert into entities values ('a', '{"id":"a","name":"Legacy Plain App","names":["Legacy Plain App"]}', 'now');
                """;
            cmd.ExecuteNonQuery();
        }
        Assert.True(FileContains(path, "Legacy Plain App"));

        using (var store = new IndexStore(path))
            Assert.Equal("Legacy Plain App", Assert.Single(store.LoadApps()).Entity.Name);
        SqliteConnection.ClearAllPools();
        Assert.False(FileContains(path, "Legacy Plain App"));
    }

    [Fact]
    public void History_file_is_not_readable()
    {
        var path = Path.Combine(_dir, "history.json");
        new SearchHistory(path).Add("divorce lawyer letters", "word", "Word");
        Assert.False(FileContains(path, "divorce"));
        Assert.Equal("divorce lawyer letters", new SearchHistory(path).Recent(1)[0].Query);
    }

    [Fact]
    public void Legacy_plain_history_is_still_read()
    {
        var path = Path.Combine(_dir, "old-history.json");
        File.WriteAllText(path, """[{"query":"calculator","appId":null,"appName":null,"at":"2026-09-27T10:00:00+00:00"}]""");
        Assert.Equal("calculator", new SearchHistory(path).Recent(1)[0].Query);
        Assert.False(FileContains(path, "calculator")); // rewritten protected on load
    }
}
