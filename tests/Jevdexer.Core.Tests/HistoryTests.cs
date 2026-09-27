using Jevdexer.Core.History;

namespace Jevdexer.Core.Tests;

public class SearchHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"jevdexer-history-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Newest_first_and_a_repeated_query_moves_to_the_top()
    {
        var h = new SearchHistory(_path);
        h.Add("video games", "cs2", "Counter-Strike 2");
        h.Add("record my screen", "obs", "OBS Studio");
        h.Add("Video  Games ", "apex", "Apex Legends");

        Assert.Equal(["Video  Games", "record my screen"], h.Recent(10).Select(e => e.Query));
        Assert.Equal("Apex Legends", h.Recent(1)[0].AppName);
    }

    [Fact]
    public void Survives_a_restart_and_clears()
    {
        new SearchHistory(_path).Add("calc", "calc", "Calculator");
        var reopened = new SearchHistory(_path);
        Assert.Equal(1, reopened.Count);

        reopened.Clear();
        Assert.Equal(0, new SearchHistory(_path).Count);
    }

    [Fact]
    public void Keeps_at_most_the_limit_and_ignores_blank_queries()
    {
        var h = new SearchHistory(_path);
        h.Add("   ", null, null);
        for (var i = 0; i < SearchHistory.MaxStored + 5; i++) h.Add($"q{i}", null, null);
        Assert.Equal(SearchHistory.MaxStored, h.Count);
        Assert.Equal("q24", h.Recent(1)[0].Query);
    }

    [Fact]
    public void A_damaged_file_starts_empty()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Equal(0, new SearchHistory(_path).Count);
    }
}
