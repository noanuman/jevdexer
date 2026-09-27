using System.Text.RegularExpressions;

namespace Jevdexer.Content.Analysis;

/// <summary>
/// The file names a document mentions: "![](charts/revenue.png)", "see growth_chart.png",
/// "data in sales_2024.csv". The app links them to the files they name, so a chart inherits the
/// name and words of the article that uses it. Names only, lower-cased, without folders; at most
/// <see cref="Max"/>.
/// </summary>
public static partial class References
{
    public const int Max = 60;

    public static List<string> Find(string text) =>
        FileName().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(n => n.Length <= 120)
            .Distinct()
            .Take(Max)
            .ToList();

    // A name ends at the extension; folders before it (slashes) are left out by the character class.
    [GeneratedRegex(@"[\w\-.()]*\w\.(png|jpe?g|gif|svg|webp|heic|tiff?|bmp|pdf|docx?|xlsx?|pptx?|csv|md|txt|mp4|mov)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();
}
