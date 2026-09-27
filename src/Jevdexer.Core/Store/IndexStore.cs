using System.Text.Json;
using Jevdexer.Discovery;
using Microsoft.Data.Sqlite;

namespace Jevdexer.Core.Store;

/// <summary>
/// The local index: `%LOCALAPPDATA%\Jevdexer\index.db`, SQLite. Entities as JSON (discovery's
/// shape changes faster than a column schema should), labels with every tag probability, and
/// the cached tag list. Per-user by location. The entity JSON and label probabilities are
/// DPAPI-protected per row; ids, versions and timestamps are not, they reveal nothing.
/// Older plain-text rows are read as plain and rewritten protected on the next save.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;

    // Populate: Entity exposes its lists as get-only properties.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
    };

    public IndexStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Migrate();
    }

    public static IndexStore OpenDefault() => new(Path.Combine(Settings.DataDir, "index.db"));

    private void Migrate()
    {
        Exec("""
            create table if not exists meta(key text primary key, value text not null);
            create table if not exists entities(id text primary key, json text not null, seen_at text not null);
            create table if not exists labels(
                entity_id text primary key references entities(id) on delete cascade,
                model text not null, tag_list_version text not null, input_hash text not null,
                background real not null, tags_json text not null, labeled_at text not null);
            pragma foreign_keys = on;
            pragma secure_delete = on;
            """);
        Exec($"insert or ignore into meta(key, value) values ('schema', '{SchemaVersion}')");
        ProtectLegacyRows();
    }

    /// <summary>
    /// Plain-text rows are rewritten protected, then VACUUM rebuilds the file so the old
    /// plain text does not survive in free pages. `secure_delete` keeps later overwrites clean too.
    /// </summary>
    private void ProtectLegacyRows()
    {
        var entities = Rows("select id, json from entities where json not like 'dpapi:%'");
        var labels = Rows("select entity_id, tags_json from labels where tags_json not like 'dpapi:%'");
        if (entities.Count == 0 && labels.Count == 0) return;

        using (var tx = _db.BeginTransaction())
        {
            foreach (var (id, json) in entities)
                Exec("update entities set json = $v where id = $id", ("$v", Protector.Protect(json)), ("$id", id));
            foreach (var (id, json) in labels)
                Exec("update labels set tags_json = $v where entity_id = $id", ("$v", Protector.Protect(json)), ("$id", id));
            tx.Commit();
        }
        Exec("vacuum");
    }

    private List<(string Id, string Value)> Rows(string sql)
    {
        var rows = new List<(string, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        return rows;
    }

    /// <summary>
    /// Makes the stored entities exactly <paramref name="entities"/>. Entities no longer found are
    /// deleted together with their labels: an uninstalled app is forgotten.
    /// </summary>
    public void ReplaceEntities(IReadOnlyList<Entity> entities)
    {
        using var tx = _db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var e in entities)
            Exec("insert into entities(id, json, seen_at) values ($id, $json, $now) on conflict(id) do update set json = $json, seen_at = $now",
                ("$id", e.Id), ("$json", Protector.Protect(JsonSerializer.Serialize(e, Json))), ("$now", now));

        var keep = entities.Select(e => e.Id).ToHashSet();
        foreach (var gone in Ids().Where(id => !keep.Contains(id)).ToList())
        {
            Exec("delete from labels where entity_id = $id", ("$id", gone));
            Exec("delete from entities where id = $id", ("$id", gone));
        }
        tx.Commit();
    }

    public IReadOnlyList<IndexedApp> LoadApps()
    {
        var labels = LoadLabels();
        var apps = new List<IndexedApp>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "select id, json from entities";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var entity = JsonSerializer.Deserialize<Entity>(Protector.Unprotect(r.GetString(1)), Json);
            if (entity is not null) apps.Add(new IndexedApp(entity, labels.GetValueOrDefault(r.GetString(0))));
        }
        return apps;
    }

    public void SaveLabels(IEnumerable<AppLabel> labels)
    {
        using var tx = _db.BeginTransaction();
        foreach (var l in labels)
            Exec("""
                insert into labels(entity_id, model, tag_list_version, input_hash, background, tags_json, labeled_at)
                values ($id, $model, $v, $hash, $bg, $tags, $at)
                on conflict(entity_id) do update set model = $model, tag_list_version = $v, input_hash = $hash,
                    background = $bg, tags_json = $tags, labeled_at = $at
                """,
                ("$id", l.EntityId), ("$model", l.Model), ("$v", l.TagListVersion), ("$hash", l.InputHash),
                ("$bg", l.Background), ("$tags", Protector.Protect(JsonSerializer.Serialize(l.Tags))), ("$at", l.LabeledAt.ToString("O")));
        tx.Commit();
    }

    /// <summary>A catalog cache (e.g. winget descriptions by "id|hash8"). DPAPI-protected: it lists installed apps.</summary>
    public Dictionary<string, string> LoadCatalog(string name)
    {
        var stored = Scalar($"select value from meta where key = 'catalog:{name}'");
        if (stored is null) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Protector.Unprotect(stored)) ?? [];
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            return []; // a cache: losing it only costs refetching
        }
    }

    public void SaveCatalog(string name, Dictionary<string, string> entries) =>
        Exec("insert into meta(key, value) values ($k, $v) on conflict(key) do update set value = $v",
            ("$k", $"catalog:{name}"), ("$v", Protector.Protect(JsonSerializer.Serialize(entries))));

    public TagList? LoadTagList()
    {
        var json = Scalar("select value from meta where key = 'tag_list'");
        return json is null ? null : JsonSerializer.Deserialize<TagList>(json, Json);
    }

    /// <summary>Not protected: the tag list is the same public list for every user.</summary>
    public void SaveTagList(TagList tags) =>
        Exec("insert into meta(key, value) values ('tag_list', $v) on conflict(key) do update set value = $v",
            ("$v", JsonSerializer.Serialize(tags, Json)));

    /// <summary>Forgets everything learned by labeling. Entities stay; they are rediscovered anyway.</summary>
    public void WipeLabels() => Exec("delete from labels");

    private Dictionary<string, AppLabel> LoadLabels()
    {
        var result = new Dictionary<string, AppLabel>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "select entity_id, model, tag_list_version, input_hash, background, tags_json, labeled_at from labels";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result[r.GetString(0)] = new AppLabel
            {
                EntityId = r.GetString(0),
                Model = r.GetString(1),
                TagListVersion = r.GetString(2),
                InputHash = r.GetString(3),
                Background = r.GetDouble(4),
                Tags = JsonSerializer.Deserialize<Dictionary<string, double>>(Protector.Unprotect(r.GetString(5))) ?? [],
                LabeledAt = DateTimeOffset.Parse(r.GetString(6)),
            };
        }
        return result;
    }

    private List<string> Ids()
    {
        var ids = new List<string>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "select id from entities";
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private string? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose() => _db.Dispose();
}
