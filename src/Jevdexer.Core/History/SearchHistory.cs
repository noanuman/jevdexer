using System.Text.Json;

namespace Jevdexer.Core.History;

/// <summary>
/// Recent searches: `%LOCALAPPDATA%\Jevdexer\history.json`, DPAPI-protected as a whole.
/// A search is recorded when the user opens a result from it. Local only: never sent anywhere,
/// whatever the Smart search setting.
/// </summary>
public sealed class SearchHistory
{
    public const int MaxStored = 20;

    public sealed record Entry(string Query, string? AppId, string? AppName, DateTimeOffset At);

    private readonly string _path;
    private List<Entry> _entries;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SearchHistory(string path)
    {
        _path = path;
        _entries = Load(path);
        if (_entries.Count > 0 && !Protector.IsProtected(File.ReadAllText(path))) Save(); // encrypt an older plain-text file
    }

    public static SearchHistory OpenDefault() => new(Path.Combine(Settings.DataDir, "history.json"));

    public int Count => _entries.Count;

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<Entry> Recent(int max) => _entries.Take(max).ToList();

    /// <summary>
    /// Adds a search to the top. The same query (ignoring case and spacing) is moved, not
    /// duplicated. Blank queries are ignored.
    /// </summary>
    public void Add(string query, string? appId, string? appName)
    {
        var q = query.Trim();
        if (q.Length == 0) return;
        _entries.RemoveAll(e => Same(e.Query, q));
        _entries.Insert(0, new Entry(q, appId, appName, DateTimeOffset.Now));
        if (_entries.Count > MaxStored) _entries = _entries.Take(MaxStored).ToList();
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static List<Entry> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(Protector.Unprotect(File.ReadAllText(path)), Json) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            return []; // damaged, or protected by another Windows user: must not stop search
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, Protector.Protect(JsonSerializer.Serialize(_entries, Json)));
    }
}
