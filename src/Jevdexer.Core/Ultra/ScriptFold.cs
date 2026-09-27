using System.Globalization;
using System.Text;

namespace Jevdexer.Core.Ultra;

/// <summary>
/// One spelling for keyword matching: Serbian (and Russian, Ukrainian, Bulgarian) Cyrillic to Latin,
/// then accents off. "ранг матрица", "rang matrica" and "rešenje"/"resenje" meet. Serbian
/// transliteration is one to one (lj, nj, dž are single Cyrillic letters). Used for matching only;
/// what the user sees keeps its script.
/// </summary>
public static class ScriptFold
{
    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['ђ'] = "dj", ['е'] = "e", ['ж'] = "z", ['з'] = "z", ['и'] = "i",
        ['ј'] = "j", ['к'] = "k", ['л'] = "l", ['љ'] = "lj", ['м'] = "m", ['н'] = "n", ['њ'] = "nj", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['ћ'] = "c", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "c", ['џ'] = "dz", ['ш'] = "s",
        // Russian, Ukrainian, Bulgarian extras
        ['й'] = "j", ['ы'] = "y", ['э'] = "e", ['ю'] = "ju", ['я'] = "ja", ['ё'] = "e", ['щ'] = "sc", ['ъ'] = "", ['ь'] = "",
        ['є'] = "je", ['і'] = "i", ['ї'] = "ji", ['ґ'] = "g",
    };

    public static string Fold(string lowercase)
    {
        var sb = new StringBuilder(lowercase.Length);
        foreach (var c in lowercase)
        {
            if (Cyrillic.TryGetValue(c, out var latin)) sb.Append(latin);
            else if (c == 'đ') sb.Append("dj");
            else sb.Append(c);
        }
        // Accents off: č → c, é → e.
        var decomposed = sb.ToString().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        return plain.ToString();
    }
}
