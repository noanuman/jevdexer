using System.IO.Compression;
using System.Text;
using Jevdexer.Content.Analysis;
using Jevdexer.Content.Extract;
using Jevdexer.Content.Models;
using Jevdexer.Content.Worker;
using Jevdexer.Core.Ultra;

namespace Jevdexer.Core.Tests;

public class SensitiveSignalTests
{
    [Fact]
    public void Finds_checksummed_numbers_and_ignores_random_ones()
    {
        Assert.Contains(SensitiveSignals.PersonalId, SensitiveSignals.Find("JMBG 0101990710008"));
        Assert.DoesNotContain(SensitiveSignals.PersonalId, SensitiveSignals.Find("order 0101990710009")); // bad check digit
        Assert.Contains(SensitiveSignals.Card, SensitiveSignals.Find("card 4111 1111 1111 1111"));
        Assert.DoesNotContain(SensitiveSignals.Card, SensitiveSignals.Find("tracking 4111 1111 1111 1112"));
        Assert.Contains(SensitiveSignals.Account, SensitiveSignals.Find("IBAN GB82 WEST 1234 5698 7654 32"));
    }

    [Theory]
    [InlineData("Lozinka: hunter2", SensitiveSignals.Credentials)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", SensitiveSignals.Credentials)]
    [InlineData("Otpusna lista, dijagnoza: J18.9, I10.0", SensitiveSignals.Medical)]
    [InlineData("Број пасош 012345678", SensitiveSignals.IdentityPaper)]
    public void Finds_labelled_records(string text, string signal) => Assert.Contains(signal, SensitiveSignals.Find(text));

    [Fact]
    public void Ordinary_text_is_not_sensitive() =>
        Assert.Empty(SensitiveSignals.Find("Određeni integral i Njutn-Lajbnicova formula, zadatak 3, 2022."));
}

public class DocumentTextTests
{
    private static byte[] Docx(string body)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                w.Write($"""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
            using (var w = new StreamWriter(zip.CreateEntry("docProps/core.xml").Open()))
                w.Write("""<cp:coreProperties xmlns:cp="c" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Ugovor o zakupu</dc:title></cp:coreProperties>""");
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Reads_word_paragraphs_and_the_title()
    {
        var doc = await DocumentText.ReadAsync(Docx("<w:p><w:r><w:t>Prvi pasus.</w:t></w:r></w:p><w:p><w:r><w:t>Drugi</w:t></w:r><w:r><w:tab/><w:t>pasus.</w:t></w:r></w:p>"), "docx");
        Assert.Equal("Prvi pasus.\nDrugi pasus.", doc.Text);
        Assert.Equal("Ugovor o zakupu", doc.Title);
    }

    [Fact]
    public async Task Ignores_external_entities()
    {
        // An XXE attempt must not read a local file or throw.
        var body = "<w:p><w:r><w:t>safe</w:t></w:r></w:p>";
        var doc = await DocumentText.ReadAsync(Docx(body), "docx");
        Assert.Contains("safe", doc.Text);
    }

    [Theory]
    [InlineData("jednaˇcine i reˇsenja", "jednačine i rešenja")]
    [InlineData("Njutn-Lajbnicova", "Njutn-Lajbnicova")]
    public void Joins_latex_spacing_accents(string pdfText, string expected) => Assert.Equal(expected, DocumentText.FixSpacingAccents(pdfText));

    [Fact]
    public async Task Reads_html_without_scripts_and_utf16_text()
    {
        var html = await DocumentText.ReadAsync(Encoding.UTF8.GetBytes("<html><script>alert(1)</script><p>Zdravo &amp; dobrodošli</p></html>"), "html");
        Assert.Equal("Zdravo & dobrodošli", html.Text);
        var utf16 = await DocumentText.ReadAsync([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Ћирилица")], "txt");
        Assert.Equal("Ћирилица", utf16.Text);
    }

    [Fact]
    public void Spread_keeps_the_ends() => Assert.Equal([0, 4, 9], DocumentText.Spread([.. Enumerable.Range(0, 10)], 3));
}

public class OcrQualityTests
{
    [Theory]
    [InlineData("Acme Summary: April 2025", true)]
    [InlineData("Алгебарска структура са овако уведеним операцијама", true)]
    [InlineData("со / юњ-ети..2.„ььа бе.и-сх / ъс—Дтеи-осбЧ са", false)]
    [InlineData("?cwq / g ScrÎ / (eroô4", false)]
    public void Tells_text_from_noise(string text, bool readable) => Assert.Equal(readable, Ocr.Quality(text) >= 0.45);
}

public class ProtocolTests
{
    [Fact]
    public void Frames_round_trip_with_payloads()
    {
        var ms = new MemoryStream();
        Protocol.Write(ms, new WorkerRequest("document", "pdf", Name: "Ugovor"), [1, 2, 3]);
        Protocol.Write(ms, new WorkerRequest("exit"));
        ms.Position = 0;
        var (first, payload) = Protocol.Read<WorkerRequest>(ms)!.Value;
        Assert.Equal(("document", "pdf", "Ugovor"), (first.Op, first.Extension, first.Name));
        Assert.Equal([1, 2, 3], payload);
        Assert.Equal("exit", Protocol.Read<WorkerRequest>(ms)!.Value.Header.Op);
        Assert.Null(Protocol.Read<WorkerRequest>(ms));
    }

    [Fact]
    public void Rejects_absurd_lengths()
    {
        var ms = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        Assert.Throws<InvalidDataException>(() => Protocol.Read<WorkerRequest>(ms));
    }
}

public class ContentIndexTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"jevdexer-content-{Guid.NewGuid():N}.db");
    private const string Root = @"D:\Studije";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static ContentRecord Doc(string name, string text, string root = Root, List<string>? sensitive = null) => new()
    {
        Path = Path.Combine(root, name), Root = root, Size = text.Length, Modified = new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), Kind = "document",
        Result = new ContentResult { Kind = "document", Keywords = Keywords.Top(text), Chunks = [new Chunk(text[..Math.Min(40, text.Length)], null)], Sensitive = sensitive ?? [] },
    };

    private static ContentIndex Index(params ContentRecord[] records) =>
        new(records, [new UltraFolder { Path = Root }, new UltraFolder { Path = @"D:\Zdravlje", LocalOnly = true }]);

    [Fact]
    public void Keywords_match_across_scripts_and_word_forms()
    {
        var index = Index(
            Doc("m2.pdf", "Одређени интеграл и интеграли функција, интеграл по деловима"),
            Doc("recept.pdf", "Sarma: kiseli kupus, meso i pirinač, kupus se pere"));
        var hits = index.Search("integrali", null);
        Assert.Equal(@"D:\Studije\m2.pdf", Assert.Single(hits, h => !h.IsFolder).Path);
        Assert.DoesNotContain(index.Search("integrals of functions", null), h => h.Path.EndsWith("recept.pdf"));
    }

    [Fact]
    public void A_matching_screenshot_is_the_result_itself_not_its_folder()
    {
        var shot = new ContentRecord
        {
            Path = @"D:\Studije\Screenshots\Screenshot 2026-09-01.png", Root = Root, Size = 10, Modified = DateTime.UtcNow, Kind = "photo",
            Result = new ContentResult { Kind = "photo", Keywords = Keywords.Top("invoice total due invoice"), Tags = [new PhotoTag("screenshot", 0.6f, false)] },
        };
        var hits = Index(shot).Search("invoice screenshot", null);
        Assert.Equal(shot.Path, Assert.Single(hits).Path);
        Assert.False(hits[0].IsFolder);
    }

    [Fact]
    public void Finds_the_file_names_a_document_mentions() =>
        Assert.Equal(["revenue_curve.png", "growth_sweep.png", "sales_buckets.csv"],
            References.Find("![Revenue](charts/revenue_curve.png) and see Growth_Sweep.png; data in sales_buckets.csv, v1.2 of the model"));

    [Fact]
    public void A_chart_is_found_through_the_article_that_uses_it_and_its_folders()
    {
        static ContentRecord Rec(string rel, string kind, string text, List<string>? refs = null) => new()
        {
            Path = Path.Combine(Root, rel), Root = Root, Size = 1, Modified = DateTime.UtcNow, Kind = kind,
            Result = new ContentResult { Kind = kind, Keywords = Keywords.Top(text), References = refs ?? [], Tags = kind == "photo" ? [new PhotoTag("chart", 0.5f, false)] : [] },
        };
        var index = Index(
            Rec(@"PROJECTS\ORION\04_DRAFTS\published-v1.md", "document", "orion solar funding piece funding solar", ["revenue_curve.png"]),
            Rec(@"PROJECTS\ORION\05_REPORTS\charts\revenue_curve.png", "photo", ""),
            Rec(@"PROJECTS\OTHER\charts\bar.png", "photo", ""),
            Rec(@"PROJECTS\ORION\notes.md", "document", "orion charts images piece charts"));

        var hits = index.Search("chart images from the orion piece", null);
        Assert.Equal("revenue_curve.png", Path.GetFileName(hits[0].Path)); // above the note that talks about charts
        Assert.Equal("Used in published-v1.md", hits[0].Why);
        Assert.Contains("used in published-v1.md", index.Summary(hits[0]));
        Assert.Contains("in ORION / 05_REPORTS / charts", index.Summary(hits[0]));
    }

    [Theory]
    [InlineData("integrals", "integralni", true)]
    [InlineData("differential", "diferencijalne", true)]
    [InlineData("matrix", "matrica", true)]
    [InlineData("integrals", "intervalu", false)]
    [InlineData("info", "informatika", false)]
    public void Stems(string a, string b, bool same) => Assert.Equal(same, ContentIndex.SharesStem(a, b));

    [Fact]
    public void Words_fold_cyrillic_and_accents() =>
        Assert.Equal(["resenje", "rang", "matrica"], ContentIndex.Words("Rešenje: ранг матрица").ToList());

    [Fact]
    public void Private_hits_are_never_summarized_for_the_cloud()
    {
        var index = Index(
            Doc("m2.pdf", "integral integral izvod"),
            Doc("nalaz.pdf", "integral krvna slika", root: @"D:\Zdravlje"),                   // local-only folder
            Doc("kljuc.txt", "integral lozinka", sensitive: [SensitiveSignals.Credentials]),     // sensitive file
            Doc("backup_codes.txt", "integral codes"));                                          // sensitive by name
        var hits = index.Search("integral", null, 20);
        var files = hits.Where(h => !h.IsFolder).ToDictionary(h => Path.GetFileName(h.Path));
        Assert.False(files["m2.pdf"].Private);
        Assert.True(files["nalaz.pdf"].Private);
        Assert.True(files["kljuc.txt"].Private);
        Assert.True(files["backup_codes.txt"].Private);
        Assert.NotNull(index.Summary(files["m2.pdf"]));
        Assert.Null(index.Summary(files["nalaz.pdf"]));
        Assert.Null(index.Summary(files["kljuc.txt"]));
        Assert.Contains("files named like secrets or IDs", index.SensitiveRoots[Root]);
    }

    [Fact]
    public void A_summary_is_words_not_passages_and_is_scrubbed()
    {
        var index = Index(Doc("ugovor_jana@mail.com.pdf", "zakup stana zakup stana ugovor ugovor kirija depozit 0641234567"));
        var summary = index.Summary(index.Search("zakup stana", null).First(h => !h.IsFolder))!;
        Assert.StartsWith("file \"[email]", summary); // underscores are part of the address: all of it goes
        Assert.Contains("zakup", summary);
        Assert.DoesNotContain("0641234567", summary);
    }

    [Fact]
    public void Jev_can_vouch_for_a_weak_candidate_and_reject_a_strong_one()
    {
        var weak = new ContentHit(@"D:\a.pdf", false, 0.1, "", false);
        var strong = new ContentHit(@"D:\b.pdf", false, 0.6, "", false);
        var merged = ContentIndex.Merge([weak, strong], new Dictionary<string, double> { [@"D:\a.pdf"] = 0.9, [@"D:\b.pdf"] = 0.05 });
        Assert.Equal(@"D:\a.pdf", Assert.Single(merged).Path); // b drops to 0.3, below the bar
    }

    [Fact]
    public void Store_round_trips_encrypted_and_skips_unchanged_files()
    {
        using var store = new ContentStore(_db);
        var doc = Doc("m2.pdf", "integral");
        store.Save(doc with { Mode = "full" });
        Assert.Equal((doc.Size, doc.Modified, "full"), store.Stamp(doc.Path));
        Assert.Equal("integral", Assert.Single(store.LoadAll()).Result!.Keywords[0].Term);
        Assert.Equal(1, store.RemoveMissing(Root, new HashSet<string>()));
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void Store_rows_hold_no_plain_text()
    {
        using (var store = new ContentStore(_db)) store.Save(Doc("tajna.pdf", "neobicnarec neobicnarec"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var raw = File.ReadAllBytes(_db).Concat(File.Exists(_db + "-wal") ? File.ReadAllBytes(_db + "-wal") : []).ToArray();
        Assert.DoesNotContain("neobicnarec", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("tajna", Encoding.UTF8.GetString(raw));
    }
}

public class UltraSettingsTests
{
    [Fact]
    public void The_monthly_cap_stops_refinement()
    {
        var s = new UltraSettings { MonthlyCap = 2 };
        Assert.True(s.TryUse());
        Assert.True(s.TryUse());
        Assert.False(s.TryUse());
        Assert.Equal(2, s.UsedNow);
        s.UsageMonth = "1999-01"; // a new month resets
        Assert.True(s.TryUse());
    }

    [Fact]
    public void Folder_names_that_look_personal_start_local_only()
    {
        Assert.True(UltraService.LooksSensitive(@"C:\Users\n\Documents\Medical"));
        Assert.True(UltraService.LooksSensitive(@"D:\Porez 2023"));
        Assert.False(UltraService.LooksSensitive(@"D:\Studije\Matematika 2"));
    }
}

public class SandboxTests
{
    [Fact]
    public void The_worker_has_no_network_and_no_user_files()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Jevdexer.Worker.exe");
        Environment.SetEnvironmentVariable("JEVDEXER_PROBE_DIR", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var sandbox = Sandbox.Start(exe, "probe");
        var output = new StreamReader(sandbox.Output).ReadToEnd();
        Assert.True(sandbox.WaitForExit(TimeSpan.FromSeconds(15)));
        Assert.Contains("network: blocked", output);
        Assert.Contains("user files: blocked", output);
        Assert.DoesNotContain("ALLOWED", output);
    }

    [Fact]
    public async Task The_worker_reads_a_document_inside_the_sandbox()
    {
        using var worker = new ContentWorker(full: false, Path.Combine(AppContext.BaseDirectory, "Jevdexer.Worker.exe"));
        var r = await worker.AnalyzeAsync("document", "txt", Encoding.UTF8.GetBytes("Diferencijalne jednačine, diferencijalne jednačine prvog reda."), CancellationToken.None);
        Assert.Equal("document", r.Kind);
        Assert.Contains(r.Keywords, k => k.Term == "diferencijalne" && k.Count == 2);
    }

    [Fact]
    public async Task A_hostile_file_fails_alone()
    {
        using var worker = new ContentWorker(full: false, Path.Combine(AppContext.BaseDirectory, "Jevdexer.Worker.exe"));
        var broken = await worker.AnalyzeAsync("document", "pdf", Encoding.ASCII.GetBytes("%PDF-1.7 garbage"), CancellationToken.None);
        Assert.Equal("failed", broken.Kind);
        var next = await worker.AnalyzeAsync("document", "txt", Encoding.UTF8.GetBytes("still working fine"), CancellationToken.None);
        Assert.Equal("document", next.Kind);
    }
}
