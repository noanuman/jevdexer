using Jevdexer.Discovery;

namespace Jevdexer.Core;

/// <summary>What Jev said about one app at index time. Every tag probability is kept, not just the top ones.</summary>
public sealed class AppLabel
{
    public required string EntityId { get; init; }
    public required string Model { get; init; }
    public required string TagListVersion { get; init; }

    /// <summary>Hash of what was sent. A different hash means the app changed and needs relabeling.</summary>
    public required string InputHash { get; init; }

    public required double Background { get; init; }
    public required IReadOnlyDictionary<string, double> Tags { get; init; }
    public required DateTimeOffset LabeledAt { get; init; }
}

public sealed record TagInfo(string Meaning, IReadOnlyList<string> Keywords);

/// <summary>The tag list, cached locally so tag matching works offline.</summary>
public sealed record TagList(string TagListVersion, IReadOnlyDictionary<string, TagInfo> Tags);

/// <summary>An entity as the search engine sees it: discovery's record plus its label, if any.</summary>
public sealed record IndexedApp(Entity Entity, AppLabel? Label);
