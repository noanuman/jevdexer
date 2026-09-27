using Jevdexer.Core.Files;
using Jevdexer.Core.Search;

namespace Jevdexer.Core.Tests;

public class FolderSummaryTests
{
    private static FileEntry Folder(string path) => new()
    {
        Path = path, Name = Path.GetFileName(path), IsFolder = true, Extension = "",
        Key = LocalMatcher.Normalize(Path.GetFileName(path)), Modified = new DateTime(2023, 6, 1),
    };

    private static FileEntry File(string path, DateTime modified) => new()
    {
        Path = path, Name = Path.GetFileName(path), IsFolder = false,
        Extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
        Key = LocalMatcher.Normalize(Path.GetFileNameWithoutExtension(path)), Modified = modified,
    };

    private static readonly List<FileEntry> Tree =
    [
        Folder(@"D:\Archive\Porez"),
        File(@"D:\Archive\Porez\faktura_01.pdf", new DateTime(2023, 2, 1)),
        File(@"D:\Archive\Porez\faktura_02.pdf", new DateTime(2023, 3, 1)),
        File(@"D:\Archive\Porez\obracun.xlsx", new DateTime(2023, 11, 1)),
        Folder(@"D:\Pictures\Greece"),
        File(@"D:\Pictures\Greece\IMG_0001.jpg", new DateTime(2022, 8, 1)),
        File(@"D:\Pictures\Greece\IMG_0002.jpg", new DateTime(2022, 8, 2)),
        Folder(@"D:\Empty"),
    ];

    [Fact]
    public void Shortlists_by_kind_and_year_even_without_a_name_match()
    {
        var hits = FolderSummaries.Shortlist(Tree, "tax documents from 2023");
        Assert.Equal(@"D:\Archive\Porez", hits[0].Folder.Path);
        Assert.DoesNotContain(hits, h => h.Folder.Path == @"D:\Empty"); // nothing inside to describe
    }

    [Fact]
    public void Photos_find_the_photo_folder()
    {
        var hits = FolderSummaries.Shortlist(Tree, "photos from the holiday");
        Assert.Equal(@"D:\Pictures\Greece", Assert.Single(hits).Folder.Path);
    }

    [Fact]
    public void A_summary_names_the_folder_its_parent_types_samples_and_dates_but_not_the_path()
    {
        var s = FolderSummaries.Shortlist(Tree, "tax documents from 2023")[0].Summary;
        Assert.StartsWith("\"Porez\", in Archive: 3 files (2 pdf, 1 xlsx)", s);
        Assert.Contains("faktura_01", s);
        Assert.Contains("Feb 2023 to Nov 2023", s);
        Assert.DoesNotContain(@"D:\", s);
    }

    [Theory]
    [InlineData("invoice jane.doe@mail.com", "invoice [email]")]
    [InlineData("tuta_recovery_kit_jane_at_tutamail_com", "[email]")] // underscores join the whole name: all of it goes
    [InlineData("write to jane at mail dot com", "write to [email]")]
    [InlineData("call +381 64 123 4567 now", "call [phone] now")]
    [InlineData("account 1234567890", "account [number]")]
    [InlineData("iban RS35260005601001611379", "iban [account]")]
    [InlineData("see https://bank.example/x?id=1", "see [link]")]
    [InlineData("scan 2023-03 v2", "scan 2023-03 v2")]
    [InlineData("IMG_20230301_1234", "IMG_20230301_1234")]
    [InlineData("d594c234-3ac6-4a1b-9c2d-e22afdfd3642 export", "[id] export")]
    [InlineData("cache 25a5a16b2423bf7487ac5340b5b0948cef48c5f8", "cache [id]")]
    public void Scrubs_identifiers(string input, string expected) => Assert.Equal(expected, FolderSummaries.Scrub(input));

    [Fact]
    public void Merge_boosts_local_hits_and_admits_only_confident_new_ones()
    {
        var a = new FileHit(Folder(@"D:\a"), 0.5);
        var merged = FolderSummaries.Merge([a], [new FileHit(a.Entry, 0.8), new FileHit(Folder(@"D:\b"), 0.6), new FileHit(Folder(@"D:\c"), 0.2)]);
        Assert.Equal([@"D:\a", @"D:\b"], merged.Select(h => h.Entry.Path));
        Assert.Equal(0.9, merged[0].Score, 3);
    }
}
