using System.Numerics;
using System.Text.RegularExpressions;

namespace Jevdexer.Content.Analysis;

/// <summary>
/// Signs that a document holds personal records: checksummed ID and
/// account numbers, and the words that label identity papers, medical records, credentials and
/// keys. Checksums keep random digit runs from counting. A folder with any signal defaults to
/// local-only: its content never reaches the cloud, even scrubbed.
/// </summary>
public static partial class SensitiveSignals
{
    public const string PersonalId = "personal id number";
    public const string Card = "payment card number";
    public const string Account = "bank account number";
    public const string IdentityPaper = "identity document";
    public const string Medical = "medical record";
    public const string Credentials = "passwords or keys";

    public static List<string> Find(string text)
    {
        var found = new List<string>();
        if (Thirteen().Matches(text).Any(m => IsJmbg(m.Value))) found.Add(PersonalId);
        if (CardLike().Matches(text).Any(m => Luhn(m.Value))) found.Add(Card);
        if (IbanLike().Matches(text).Any(m => IbanValid(m.Value))) found.Add(Account);
        if (IdentityWords().IsMatch(text)) found.Add(IdentityPaper);
        if (MedicalWords().IsMatch(text) || Icd10().Matches(text).Count >= 2) found.Add(Medical);
        if (CredentialWords().IsMatch(text)) found.Add(Credentials);
        return found;
    }

    /// <summary>JMBG (Serbia and the former Yugoslavia): 13 digits, a valid birth date, mod-11 check digit.</summary>
    internal static bool IsJmbg(string s)
    {
        if (s.Length != 13 || !s.All(char.IsAsciiDigit)) return false;
        var d = s.Select(c => c - '0').ToArray();
        var day = d[0] * 10 + d[1];
        var month = d[2] * 10 + d[3];
        if (day is < 1 or > 31 || month is < 1 or > 12) return false;
        int[] w = [7, 6, 5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var m = 11 - w.Select((x, i) => x * d[i]).Sum() % 11;
        var check = m > 9 ? 0 : m;
        return check == d[12];
    }

    internal static bool Luhn(string s)
    {
        var digits = s.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19 || digits[0] is < 2 or > 6) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var v = digits[digits.Length - 1 - i];
            if (i % 2 == 1) v = v * 2 > 9 ? v * 2 - 9 : v * 2;
            sum += v;
        }
        return sum % 10 == 0;
    }

    internal static bool IbanValid(string s)
    {
        var iban = s.Replace(" ", "").ToUpperInvariant();
        if (iban.Length is < 15 or > 34) return false;
        var rearranged = iban[4..] + iban[..4];
        var digits = string.Concat(rearranged.Select(c => char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        return BigInteger.Parse(digits) % 97 == 1;
    }

    [GeneratedRegex(@"(?<!\d)\d{13}(?!\d)")] private static partial Regex Thirteen();
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")] private static partial Regex CardLike();
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b")] private static partial Regex IbanLike();

    [GeneratedRegex(@"\b(passport|pasoš|pasos|пасош|lična karta|licna karta|лична карта|identity card|id card|vozačka dozvola|возачка дозвола|driving licen[cs]e|jmbg|јмбг)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityWords();

    [GeneratedRegex(@"\b(diagnos[ie]s|dijagnoza|дијагноза|anamneza|анамнеза|patient|pacijent|пацијент|lekarski izveštaj|лекарски извештај|medical report|otpusna lista|отпусна листа|laboratorijski nalaz|лабораторијски налаз)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MedicalWords();

    [GeneratedRegex(@"\b[A-TV-Z]\d{2}\.\d\b")] private static partial Regex Icd10();

    [GeneratedRegex(@"(-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(password|lozinka|лозинка|passphrase|seed phrase|recovery (code|phrase|kit)|backup codes?|private key|api[_ ]key|secret key)\b\s*[:=])", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialWords();
}
