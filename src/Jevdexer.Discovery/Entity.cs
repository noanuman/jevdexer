namespace Jevdexer.Discovery;

/// <summary>One launchable thing, merged from every source that reported it.</summary>
public sealed class Entity
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Publisher { get; set; }
    public string? Exe { get; set; }
    public string? Args { get; set; }
    public string? Aumid { get; set; }
    public string? InstallDir { get; set; }

    /// <summary>Icon file for display when there is no exe or AUMID (Steam writes one per game).</summary>
    public string? IconPath { get; set; }
    public string? LaunchUri { get; set; }
    public VersionInfo? Version { get; set; }

    /// <summary>Catalog metadata (winget, Steam). Null when no catalog knows the entity.</summary>
    public Enrichment.EntityEnrichment? Enrichment { get; set; }

    public List<string> Sources { get; } = [];

    /// <summary>Every distinct name a source used. Useful signal for labeling.</summary>
    public List<string> Names { get; } = [];

    public Dictionary<string, string> SourceIds { get; } = [];
}

/// <summary>What an exe says about itself in its PE version resource.</summary>
public sealed record VersionInfo(string? ProductName, string? FileDescription, string? CompanyName);
