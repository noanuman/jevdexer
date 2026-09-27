using Jevdexer.Core.Search;
using Jevdexer.Discovery;

namespace Jevdexer.Core.Tests;

internal static class Fixtures
{
    public static IndexedApp App(string name, string? publisher = null, double background = 0, params (string Tag, double P)[] tags)
    {
        var e = new Entity { Id = name.ToLowerInvariant().Replace(' ', '-'), Name = name, Publisher = publisher, Exe = $@"C:\x\{name}.exe" };
        e.Names.Add(name);
        var label = new AppLabel
        {
            EntityId = e.Id, Model = "test", TagListVersion = "v", InputHash = "h", Background = background,
            Tags = tags.ToDictionary(t => t.Tag, t => t.P), LabeledAt = DateTimeOffset.UnixEpoch,
        };
        return new IndexedApp(e, label);
    }

    public static readonly TagList Tags = new("v", new Dictionary<string, TagInfo>
    {
        ["game"] = new("a video game", ["game", "games", "video games", "play"]),
        ["video_editing"] = new("editing videos", ["video editing", "video editor"]),
        ["screen_recording"] = new("recording the screen", ["screen recording", "record screen"]),
        ["video_player"] = new("watching videos", ["watch video", "play video"]),
    });
}

public class LocalMatcherTests
{
    [Theory]
    [InlineData("chrome", "Google Chrome", 0.92)]
    [InlineData("google", "Google Chrome", 0.97)]
    [InlineData("vsc", "Visual Studio Code", 0.9)]
    [InlineData("code studio", "Visual Studio Code", 0.9)]
    [InlineData("photshop", "Photoshop", 0.75)]
    [InlineData("prem", "Command Prompt", 0)] // four letters: no typo tolerance
    [InlineData("zzz", "Steam", 0)]
    public void Scores_names(string query, string name, double expected) =>
        Assert.Equal(expected, LocalMatcher.Score(query, [name], null));

    [Fact]
    public void Transposition_counts_as_one_edit() => Assert.Equal(1, LocalMatcher.Distance("chorme", "chrome"));
}

public class TagMatcherTests
{
    [Fact]
    public void Exact_phrase_and_scattered_words_map_to_tags()
    {
        Assert.Equal(1.0, TagMatcher.FromKeywords("video games", Fixtures.Tags)["game"]);
        Assert.Equal(0.8, TagMatcher.FromKeywords("record my screen", Fixtures.Tags)["screen_recording"]);
    }

    [Fact]
    public void Part_of_a_multi_word_keyword_is_not_a_match()
    {
        // "video" alone must not pull in "video games" or "watch video".
        var tags = TagMatcher.FromKeywords("video editing", Fixtures.Tags);
        Assert.Equal(["video_editing"], tags.Keys);
    }
}

public class RankerTests
{
    private static readonly IndexedApp[] Apps =
    [
        Fixtures.App("Microsoft Clipchamp", "Microsoft", 0, ("video_editing", 0.97)),
        Fixtures.App("Counter-Strike 2", "Valve", 0, ("game", 0.99)),
        Fixtures.App("Game Overlay Renderer", "Valve", 0.95, ("game", 0.9)),
        Fixtures.App("Microsoft Store", "Microsoft", 0, ("game_launcher", 0.8)),
    ];

    [Fact]
    public void A_wiped_label_is_recovered_by_jev()
    {
        var wiped = Fixtures.App("Microsoft Clipchamp", "Microsoft");
        var results = Ranker.Rank("video editing", [wiped], new Dictionary<string, double> { ["video_editing"] = 1 },
            new Dictionary<string, double> { [wiped.Entity.Id] = 0.88 });
        Assert.InRange(Assert.Single(results).Score, 0.88, 0.92);
    }

    [Fact]
    public void Jev_orders_apps_the_tags_tie_on()
    {
        IndexedApp[] messengers =
        [
            Fixtures.App("Telegram", "Telegram", 0, ("messaging", 0.99)),
            Fixtures.App("Viber", "Rakuten", 0, ("messaging", 0.99)),
            Fixtures.App("WhatsApp", "WhatsApp", 0, ("messaging", 0.98)),
        ];
        var jev = new Dictionary<string, double> { [messengers[0].Entity.Id] = 0.25, [messengers[1].Entity.Id] = 0.19, [messengers[2].Entity.Id] = 0.65 };
        var results = Ranker.Rank("that green messaging app", messengers, new Dictionary<string, double> { ["messaging"] = 1 }, jev);
        Assert.Equal("WhatsApp", results[0].App.Entity.Name);
    }

    [Fact]
    public void Jev_noise_below_the_floor_is_ignored()
    {
        var results = Ranker.Rank("video editing", Apps, new Dictionary<string, double> { ["video_editing"] = 1 },
            new Dictionary<string, double> { ["microsoft-store"] = 0.37 });
        Assert.DoesNotContain(results, r => r.App.Entity.Name == "Microsoft Store");
    }

    [Fact]
    public void Background_components_sink_below_real_apps()
    {
        var results = Ranker.Rank("video games", Apps, new Dictionary<string, double> { ["game"] = 1 }, null);
        Assert.Equal("Counter-Strike 2", results[0].App.Entity.Name);
        Assert.True(results.Single(r => r.App.Entity.Name == "Game Overlay Renderer").Score < 0.5);
    }

    [Fact]
    public void Typing_a_name_wins_over_semantics()
    {
        var results = Ranker.Rank("microsoft store", Apps, new Dictionary<string, double> { ["game"] = 1 }, null);
        Assert.Equal("Microsoft Store", results[0].App.Entity.Name);
    }
}

public class SearchEngineTests
{
    [Fact]
    public void A_confident_name_match_never_needs_the_network()
    {
        using var backend = new Remote.JevClient("test-key");
        var engine = new SearchEngine(backend);
        engine.Load([Fixtures.App("Google Chrome", "Google")], Fixtures.Tags);

        Assert.False(engine.NeedsRemote("chrome", engine.Local("chrome")));
        Assert.True(engine.NeedsRemote("browse the web", engine.Local("browse the web")));
        Assert.False(engine.NeedsRemote("ch", engine.Local("ch"))); // too short to be worth a call
    }

    [Fact]
    public void Without_a_backend_nothing_goes_remote()
    {
        var engine = new SearchEngine(null);
        engine.Load([Fixtures.App("Google Chrome")], Fixtures.Tags);
        Assert.False(engine.NeedsRemote("browse the web", engine.Local("browse the web")));
    }
}
