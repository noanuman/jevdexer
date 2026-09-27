namespace Jevdexer.Discovery;

/// <summary>
/// Minimal reader for Valve's text KeyValues format (`libraryfolders.vdf`, `appmanifest_*.acf`):
/// quoted keys, quoted values or `{ }` blocks, `\\` and `\"` escapes, `//` comments.
/// </summary>
internal static class Vdf
{
    public sealed class Node
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => Values.GetValueOrDefault(key);
        public Node? Child(string key) => Children.GetValueOrDefault(key);
    }

    public static Node Parse(string text)
    {
        var pos = 0;
        return ReadBlock(text, ref pos);
    }

    private static Node ReadBlock(string s, ref int pos)
    {
        var node = new Node();
        while (true)
        {
            var key = NextToken(s, ref pos);
            if (key is null || key == "}") return node;

            var value = NextToken(s, ref pos);
            if (value is null) return node;
            if (value == "{") node.Children[key] = ReadBlock(s, ref pos);
            else node.Values[key] = value;
        }
    }

    /// <summary>Returns a quoted string's content, "{" or "}", or null at end of input.</summary>
    private static string? NextToken(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c is '{' or '}') { pos++; return c.ToString(); }
            if (c == '"') return ReadQuoted(s, ref pos);

            // Unquoted token: read to whitespace.
            var start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}')) pos++;
            return s[start..pos];
        }
        return null;
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        pos++; // opening quote
        var sb = new System.Text.StringBuilder();
        while (pos < s.Length && s[pos] != '"')
        {
            if (s[pos] == '\\' && pos + 1 < s.Length) pos++;
            sb.Append(s[pos++]);
        }
        pos++; // closing quote
        return sb.ToString();
    }
}
