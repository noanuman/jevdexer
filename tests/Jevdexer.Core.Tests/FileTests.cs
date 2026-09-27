using Jevdexer.Core.Files;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Tests;

public class FileIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jevdexer-files-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, bool folder = false)
    {
        var path = Path.Combine(_root, relative);
        if (folder) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        return path;
    }

    private FileRoots Rules(params string[] skippedPaths) => new([_root], skippedPaths);

    [Fact]
    public async Task Crawls_people_folders_and_skips_system_and_developer_folders()
    {
        Make(@"Work\Tax\2023\faktura_01.pdf");
        Make(@"Photos\wedding.jpg");
        Make(@"code\app\node_modules\left-pad\index.js");
        Make(@"code\app\.git\HEAD");
        Make(@"code\app\bin\Debug\app.dll");
        Make(@"Games\SomeGame\data.pak");
        Make(@"Work\~$lock.docx");

        var index = new FileIndex();
        await index.BuildAsync(Rules(Path.Combine(_root, "Games", "SomeGame")));
        var names = index.Entries.Select(e => e.Name).ToHashSet();

        Assert.Contains("faktura_01.pdf", names);
        Assert.Contains("wedding.jpg", names);
        Assert.Contains("Tax", names); // folders are entries too
        Assert.DoesNotContain("index.js", names);
        Assert.DoesNotContain("HEAD", names);
        Assert.DoesNotContain("app.dll", names);
        Assert.DoesNotContain("data.pak", names); // an indexed app's install folder
        Assert.DoesNotContain("~$lock.docx", names);
        Assert.True(index.Ready);
    }

    [Fact]
    public async Task Updates_one_folder_and_whole_trees()
    {
        Make(@"Docs\a.txt");
        var index = new FileIndex();
        await index.BuildAsync(Rules());

        Make(@"Docs\b.txt");
        index.RefreshFolder(Path.Combine(_root, "Docs"));
        Assert.Contains(index.Entries, e => e.Name == "b.txt");

        Make(@"Docs\New\deep\c.txt");
        index.AddTree(Path.Combine(_root, "Docs", "New"));
        Assert.Contains(index.Entries, e => e.Name == "c.txt");

        Directory.Delete(Path.Combine(_root, "Docs", "New"), recursive: true);
        index.RemoveTree(Path.Combine(_root, "Docs", "New"));
        index.RefreshFolder(Path.Combine(_root, "Docs"));
        Assert.DoesNotContain(index.Entries, e => e.Name is "c.txt" or "New" or "deep");
    }

    [Fact]
    public async Task Change_notifications_from_skipped_folders_never_enter_the_index()
    {
        Make(@"Docs\a.txt");
        var index = new FileIndex();
        await index.BuildAsync(Rules());
        var (files, folders) = (index.FileCount, index.FolderCount);

        // What Windows and apps touch all day: caches, temp, package folders, dot-folders.
        foreach (var noisy in new[] { @"AppData\Local\Cache\x.tmp2", @"proj\node_modules\pkg\i.js", @".cache\blob", @"tmp\scratch.txt" })
        {
            var path = Make(noisy);
            index.RefreshFolder(Path.GetDirectoryName(path)!);
            index.AddTree(Path.GetDirectoryName(path)!);
        }
        var hidden = Make(@"Secret\h.txt");
        File.SetAttributes(Path.GetDirectoryName(hidden)!, FileAttributes.Hidden | FileAttributes.Directory);
        index.RefreshFolder(Path.GetDirectoryName(hidden)!);

        Assert.Equal((files, folders), (index.FileCount, index.FolderCount));
    }

    [Fact]
    public void Nested_roots_are_crawled_once()
    {
        var inner = Make(@"OneDrive\Documents", folder: true);
        var rules = new FileRoots([Path.Combine(_root, "OneDrive"), inner], []);
        Assert.Single(rules.Roots);
    }
}

public class FileMatcherTests
{
    private static FileEntry E(string path, bool folder = false, int daysAgo = 0)
    {
        var name = Path.GetFileName(path);
        return new FileEntry
        {
            Path = path, Name = name, IsFolder = folder, Modified = DateTime.UtcNow.AddDays(-daysAgo),
            Key = LocalMatcher.Normalize(folder ? name : Path.GetFileNameWithoutExtension(name)),
            Extension = folder ? "" : Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
        };
    }

    private static readonly FileEntry[] Files =
    [
        E(@"D:\Work\Tax\2023\faktura_01.pdf"),
        E(@"D:\Work\Reports\Q3 report.pdf", daysAgo: 2),
        E(@"D:\Work\Reports\Q3 report.docx", daysAgo: 1),
        E(@"C:\Users\n\Pictures\wedding.jpg"),
        E(@"D:\Work\Tax", folder: true),
        E(@"D:\Work\Tax\2023", folder: true),
    ];

    [Fact]
    public void Name_prefix_and_words() =>
        Assert.Equal("wedding.jpg", FileMatcher.Search(Files, "wedd", folders: false, 5)[0].Entry.Name);

    [Fact]
    public void A_file_type_word_narrows_the_match()
    {
        var hits = FileMatcher.Search(Files, "report pdf", folders: false, 5);
        Assert.Equal("Q3 report.pdf", hits[0].Entry.Name);
    }

    [Fact]
    public void Path_words_find_files_inside_named_folders()
    {
        var hits = FileMatcher.Search(Files, "tax faktura", folders: false, 5);
        Assert.Equal("faktura_01.pdf", Assert.Single(hits).Entry.Name);
    }

    [Fact]
    public void Folders_and_files_are_searched_separately()
    {
        Assert.Equal("Tax", FileMatcher.Search(Files, "tax", folders: true, 5)[0].Entry.Name);
        Assert.DoesNotContain(FileMatcher.Search(Files, "tax", folders: false, 5), h => h.Entry.IsFolder);
    }

    [Fact]
    public void Build_output_ranks_below_real_files()
    {
        FileEntry[] files = [E(@"D:\app\Jevdexer.dll"), E(@"D:\app\Jevdexer.exe", daysAgo: 30)];
        var hits = FileMatcher.Search(files, "jevdexer", folders: false, 5);
        Assert.Equal("Jevdexer.exe", hits[0].Entry.Name);
        Assert.Equal(2, hits.Count); // still findable
    }

    [Fact]
    public void Ties_go_to_the_most_recent()
    {
        var hits = FileMatcher.Search(Files, "q3 report", folders: false, 5);
        Assert.Equal("Q3 report.docx", hits[0].Entry.Name); // modified more recently
    }
}
