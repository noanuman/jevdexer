using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jevdexer.Content.Worker;
using Microsoft.Data.Sqlite;

namespace Jevdexer.Core.Ultra;

/// <summary>One file Ultra Search has looked at.</summary>
public sealed record ContentRecord
{
    public required string Path { get; init; }
    /// <summary>The selected folder it belongs to.</summary>
    public required string Root { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    /// <summary>"document", "photo", "photo-unsampled" (counted for its folder, not analyzed), "failed".</summary>
    public required string Kind { get; init; }
    /// <summary>The mode that analyzed it ("light" or "full"): switching mode re-analyzes.</summary>
    public string Mode { get; init; } = "light";
    public ContentResult? Result { get; init; }
}

/// <summary>
/// `content.db`: Ultra Search's derived data, one DPAPI-protected row per file: keywords,
/// chunk heads and vectors, photo tags. Never raw text. Rows are keyed by a hash of the path; size
/// and modification time stay plain so a rescan can skip unchanged files without decrypting.
/// A separate file from index.db so the wipe is one delete (<see cref="Wipe"/>).
/// </summary>
public sealed class ContentStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new(); // the scan writes while searches reload: one connection, one caller at a time
    private readonly string _path;

    private static readonly JsonSerializerOptions Json = Protocol.Json;

    public ContentStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            create table if not exists items(
                key text primary key, root_key text not null, size integer not null, modified text not null, mode text not null, data text not null);
            create index if not exists items_root on items(root_key);
            pragma secure_delete = on;
            pragma journal_mode = wal;
            """);
    }

    public static string DefaultPath => System.IO.Path.Combine(Settings.DataDir, "content.db");

    public static ContentStore OpenDefault() => new(DefaultPath);

    public static string Key(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));

    /// <summary>Size, time and mode last stored for a path, or null: enough to skip an unchanged file.</summary>
    public (long Size, DateTime Modified, string Mode)? Stamp(string path)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select size, modified, mode from items where key = $k";
            cmd.Parameters.AddWithValue("$k", Key(path));
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(2)) : null;
        }
    }

    public void Save(ContentRecord record)
    {
        lock (_lock) Exec("insert or replace into items(key, root_key, size, modified, mode, data) values ($k, $r, $s, $m, $mode, $d)",
            ("$k", Key(record.Path)), ("$r", Key(record.Root)), ("$s", record.Size), ("$m", record.Modified.ToString("O")), ("$mode", record.Mode),
            ("$d", Protector.Protect(JsonSerializer.Serialize(record, Json))));
    }

    public List<ContentRecord> LoadAll()
    {
        lock (_lock)
        {
            var list = new List<ContentRecord>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "select data from items";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<ContentRecord>(Protector.Unprotect(r.GetString(0)), Json) is { } rec) list.Add(rec);
                }
                catch (Exception e) when (e is JsonException or CryptographicException or FormatException)
                {
                    // a row another Windows user wrote, or damaged: skip; the next scan replaces it
                }
            }
            return list;
        }
    }

    /// <summary>Forgets files that are gone from a folder (deleted or renamed).</summary>
    public int RemoveMissing(string root, IReadOnlySet<string> presentKeys)
    {
        lock (_lock)
        {
            var stale = new List<string>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "select key from items where root_key = $r";
                cmd.Parameters.AddWithValue("$r", Key(root));
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!presentKeys.Contains(r.GetString(0))) stale.Add(r.GetString(0));
            }
            foreach (var k in stale) Exec("delete from items where key = $k", ("$k", k));
            return stale.Count;
        }
    }

    public void RemoveRoot(string root)
    {
        lock (_lock) Exec("delete from items where root_key = $r", ("$r", Key(root)));
    }

    public int Count(string? root = null)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = root is null ? "select count(*) from items" : "select count(*) from items where root_key = $r";
            if (root is not null) cmd.Parameters.AddWithValue("$r", Key(root));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Removes every Ultra Search summary: the whole database file and its journal.</summary>
    public void Wipe()
    {
        lock (_lock)
        {
            _db.Close();
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
