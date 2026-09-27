using Jevdexer.Core.Remote;

namespace Jevdexer.Core.Search;

/// <summary>
/// Runs a query in two steps:
/// <see cref="Local"/> on every keystroke (layers 1 and 2, no network), then
/// <see cref="RefineAsync"/> once the user pauses, only when <see cref="NeedsRemote"/> says the
/// local answer is not confident. Remote answers are cached per query for the app set they
/// were computed against.
/// </summary>
public sealed class SearchEngine(JevClient? backend)
{
    private IReadOnlyList<IndexedApp> _apps = [];
    private TagList? _tags;
    private readonly Dictionary<string, JevClient.SearchResponse> _cache = [];

    public int AppCount => _apps.Count;

    public IReadOnlyList<IndexedApp> Apps => _apps;

    /// <summary>The indexed app with this id, or null (uninstalled since, or never indexed).</summary>
    public IndexedApp? Find(string id) => _apps.FirstOrDefault(a => a.Entity.Id == id);

    /// <summary>Swaps in a new index. Cached remote answers are dropped: they scored the old app set.</summary>
    public void Load(IReadOnlyList<IndexedApp> apps, TagList? tags)
    {
        lock (_cache)
        {
            _apps = apps;
            _tags = tags;
            _cache.Clear();
        }
    }

    public List<SearchResult> Local(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var queryTags = _tags is null ? new Dictionary<string, double>() : TagMatcher.FromKeywords(query, _tags);
        return Ranker.Rank(query, _apps, queryTags, jev: null);
    }

    /// <summary>A confident name match never goes to the network; neither do very short queries.</summary>
    public bool NeedsRemote(string query, IReadOnlyList<SearchResult> local) =>
        backend is not null
        && LocalMatcher.Normalize(query).Length >= 3
        && (local.Count == 0 || local[0].Local < LocalMatcher.Confident);

    /// <summary>Layers 2 (Jev's query-to-tag map) and 3 (Jev over every app). Throws on network failure; callers keep the local list.</summary>
    public async Task<List<SearchResult>> RefineAsync(string query, CancellationToken ct)
    {
        if (backend is null) throw new InvalidOperationException("no backend configured");
        var key = LocalMatcher.Normalize(query);
        IReadOnlyList<IndexedApp> apps;
        JevClient.SearchResponse? response;
        lock (_cache)
        {
            apps = _apps;
            _cache.TryGetValue(key, out response);
        }

        if (response is null)
        {
            var lines = apps.Select(a => new JevClient.SearchRequestApp(a.Entity.Id, AppDescriber.Line(a.Entity))).ToList();
            response = await backend.SearchAsync(query, lines, ct);
            lock (_cache)
            {
                if (ReferenceEquals(apps, _apps)) _cache[key] = response;
            }
        }
        return Ranker.Rank(query, apps, response.QueryTags, response.Apps);
    }
}
