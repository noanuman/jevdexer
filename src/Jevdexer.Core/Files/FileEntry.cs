namespace Jevdexer.Core.Files;

/// <summary>One file or folder. Kept small: there can be hundreds of thousands in memory.</summary>
public sealed class FileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required bool IsFolder { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    /// <summary>Name without extension, normalized for matching (see <see cref="Search.LocalMatcher.Normalize"/>).</summary>
    public required string Key { get; init; }

    /// <summary>Lower-case extension without the dot; empty for folders.</summary>
    public required string Extension { get; init; }

    public string Parent => System.IO.Path.GetDirectoryName(Path) ?? "";
}
