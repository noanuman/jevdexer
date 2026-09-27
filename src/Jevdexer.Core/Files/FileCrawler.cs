using System.IO.Enumeration;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Files;

/// <summary>
/// Walks roots with <see cref="FileSystemEnumerable{T}"/>: no <c>FileInfo</c> per entry, so hundreds
/// of thousands of entries take seconds. Hidden and system entries are skipped; reparse-point
/// folders (junctions, symlinks) are not followed, so there are no loops. OneDrive's online-only
/// files are reparse points too, and are kept: only folders are refused.
/// </summary>
public static class FileCrawler
{
    /// <summary>A safety stop: a pathological drive cannot exhaust memory.</summary>
    public const int MaxEntries = 2_000_000;

    /// <summary>Every entry under <paramref name="folder"/>, grouped by parent folder.</summary>
    public static Dictionary<string, List<FileEntry>> Crawl(string folder, FileRoots rules, bool recursive, CancellationToken ct = default)
    {
        var byParent = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        var enumerable = new FileSystemEnumerable<FileEntry>(folder, ToEntry, options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0 && rules.ShouldEnter(e.ToFullPath(), e.FileName.ToString()),
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !(e.IsDirectory && ((e.Attributes & FileAttributes.ReparsePoint) != 0 || !rules.ShouldEnter(e.ToFullPath(), e.FileName.ToString())))
                && !IsNoise(e.FileName),
        };

        var count = 0;
        try
        {
            foreach (var entry in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                var parent = entry.Parent;
                if (!byParent.TryGetValue(parent, out var list)) byParent[parent] = list = [];
                list.Add(entry);
                if (++count >= MaxEntries) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder vanished or became unreadable mid-walk; keep what was read.
        }
        return byParent;
    }

    private static FileEntry ToEntry(ref FileSystemEntry e)
    {
        var name = e.FileName.ToString();
        var isFolder = e.IsDirectory;
        var ext = isFolder ? "" : Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return new FileEntry
        {
            Path = e.ToFullPath(),
            Name = name,
            IsFolder = isFolder,
            Size = isFolder ? 0 : e.Length,
            Modified = e.LastWriteTimeUtc.UtcDateTime,
            Key = LocalMatcher.Normalize(isFolder ? name : Path.GetFileNameWithoutExtension(name)),
            Extension = ext,
        };
    }

    /// <summary>Office lock files and temporary files: never what anyone searches for.</summary>
    private static bool IsNoise(ReadOnlySpan<char> name) =>
        name.StartsWith("~$") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);
}
