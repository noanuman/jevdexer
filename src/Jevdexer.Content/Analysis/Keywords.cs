using System.Text.RegularExpressions;

namespace Jevdexer.Content.Analysis;

/// <summary>A word and how often a document uses it.</summary>
public sealed record Keyword(string Term, int Count);

/// <summary>
/// The words a document is about, without a model (Light mode's whole understanding, Full mode's
/// keyword half): letters-only words of 3+ characters, lower-cased, minus English and Serbian (Latin
/// and Cyrillic) function words, by count. Weighting across the collection (IDF) happens in the app,
/// which sees every document.
/// </summary>
public static partial class Keywords
{
    public static List<Keyword> Top(string text, int max = 40) =>
        Word().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !Stop.Contains(w))
            .GroupBy(w => w)
            .Select(g => new Keyword(g.Key, g.Count()))
            .OrderByDescending(k => k.Count).ThenByDescending(k => k.Term.Length)
            .Take(max)
            .ToList();

    [GeneratedRegex(@"\p{L}[\p{L}\p{Mn}]{2,}")]
    private static partial Regex Word();

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        // English
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one", "our", "out", "has", "have",
        "his", "how", "its", "may", "new", "now", "see", "two", "who", "did", "get", "him", "let", "put", "say", "she", "too", "use",
        "that", "with", "this", "from", "they", "will", "would", "there", "their", "what", "about", "which", "when", "your", "were",
        "been", "more", "into", "than", "then", "them", "these", "some", "such", "also", "only", "other", "each", "where", "while",
        "very", "just", "over", "after", "before", "should", "could", "those", "because", "between", "being", "does", "here",
        // Serbian, Latin
        "koji", "koja", "koje", "ili", "ako", "kao", "što", "sto", "jer", "ali", "već", "vec", "biti", "bio", "bila", "bilo",
        "sam", "smo", "ste", "su", "nije", "nisu", "ima", "iz", "za", "na", "od", "do", "po", "se", "je", "da", "ne", "to", "taj",
        "ova", "ovo", "ovaj", "tog", "toga", "tome", "kada", "kad", "gde", "gdje", "sve", "svi", "još", "jos", "može", "moze", "mogu",
        "treba", "samo", "tako", "onda", "prema", "preko", "između", "izmedju", "nakon", "pre", "posle", "dok", "čiji", "koju", "kojoj",
        "kojim", "kojih", "njih", "njegov", "njena", "njegova", "neki", "neka", "neko", "nešto", "svoj", "svoje", "svoja",
        // Serbian, Cyrillic
        "који", "која", "које", "или", "ако", "као", "што", "јер", "али", "већ", "бити", "био", "била", "било", "сам", "смо", "сте",
        "нису", "није", "има", "из", "за", "на", "од", "до", "по", "се", "је", "да", "не", "то", "тај", "ова", "ово", "овај", "тога",
        "када", "кад", "где", "све", "сви", "још", "може", "могу", "треба", "само", "тако", "онда", "према", "преко", "између", "након",
        "пре", "после", "док", "чији", "коју", "којој", "којим", "којих", "њих", "његов", "нека", "неки", "нешто", "свој", "своје",
    };
}
