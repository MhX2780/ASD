using System.Text.RegularExpressions;

namespace ASD;

/// <summary>Turns a regex pattern into a token-by-token plain-English explanation.</summary>
public static class RegexExplainer
{
    private const string Specials = "\\[](){}.*+?^$|";

    public static string Explain(string p)
    {
        var sb = new StringBuilder();
        int i = 0, depth = 0, groupNo = 0;

        void Line(string token, string meaning)
            => sb.Append(new string(' ', depth * 2)).Append(token.PadRight(Math.Max(1, 16 - depth * 2))).Append("  ").AppendLine(meaning);

        while (i < p.Length)
        {
            char c = p[i];
            switch (c)
            {
                case '\\':
                {
                    if (i + 1 >= p.Length) { Line("\\", "trailing backslash"); i++; break; }
                    char n = p[i + 1];
                    string tok = "\\" + n;
                    string? meaning = n switch
                    {
                        'd' => "a digit (0-9)", 'D' => "any character that is NOT a digit",
                        'w' => "a word character (letter, digit, underscore)", 'W' => "any character that is NOT a word character",
                        's' => "a whitespace character", 'S' => "any character that is NOT whitespace",
                        'b' => "a word boundary", 'B' => "a position that is NOT a word boundary",
                        'A' => "the very start of the text", 'z' => "the very end of the text", 'Z' => "the end of the text (before a final newline)",
                        'n' => "a newline", 'r' => "a carriage return", 't' => "a tab", 'f' => "a form feed", 'v' => "a vertical tab", 'G' => "where the previous match ended",
                        _ => null,
                    };
                    if (meaning == null)
                    {
                        if (n == 'p' || n == 'P')
                        {
                            var m = Regex.Match(p[i..], @"^\\[pP]\{[^}]*\}");
                            if (m.Success) { tok = m.Value; meaning = (n == 'p' ? "a character in Unicode category " : "a character NOT in Unicode category ") + m.Value[3..^1]; }
                        }
                        else if (n == 'x') { var m = Regex.Match(p[i..], @"^\\x[0-9A-Fa-f]{2}"); if (m.Success) { tok = m.Value; meaning = "the character with hex code " + m.Value[2..]; } }
                        else if (n == 'u') { var m = Regex.Match(p[i..], @"^\\u[0-9A-Fa-f]{4}"); if (m.Success) { tok = m.Value; meaning = "the Unicode character U+" + m.Value[2..]; } }
                        else if (n == 'k') { var m = Regex.Match(p[i..], @"^\\k[<'][^>']+[>']"); if (m.Success) { tok = m.Value; meaning = "the same text as the earlier group " + m.Value[2..]; } }
                        else if (char.IsDigit(n)) { var m = Regex.Match(p[i..], @"^\\\d+"); tok = m.Value; meaning = $"the same text as capture group {m.Value[1..]} (backreference)"; }
                        meaning ??= $"the literal character '{n}'";
                    }
                    Line(tok, meaning);
                    i += tok.Length;
                    break;
                }
                case '[':
                {
                    int j = i + 1;
                    if (j < p.Length && p[j] == '^') j++;
                    if (j < p.Length && p[j] == ']') j++;
                    while (j < p.Length && p[j] != ']') { if (p[j] == '\\') j++; j++; }
                    j = Math.Min(j, p.Length - 1);
                    var tok = p.Substring(i, j - i + 1);
                    var body = tok.Length > 2 ? tok[1..^1] : "";
                    bool neg = body.StartsWith("^");
                    if (neg) body = body[1..];
                    Line(tok, (neg ? "any character EXCEPT: " : "any one of: ") + DescribeSet(body));
                    i = j + 1;
                    break;
                }
                case '(':
                {
                    string rest = p[i..];
                    string tok, meaning;
                    if (rest.StartsWith("(?:")) { tok = "(?:"; meaning = "start of a non-capturing group"; }
                    else if (rest.StartsWith("(?=")) { tok = "(?="; meaning = "start of a positive lookahead (must be followed by…)"; }
                    else if (rest.StartsWith("(?!")) { tok = "(?!"; meaning = "start of a negative lookahead (must NOT be followed by…)"; }
                    else if (rest.StartsWith("(?<=")) { tok = "(?<="; meaning = "start of a positive lookbehind (must be preceded by…)"; }
                    else if (rest.StartsWith("(?<!")) { tok = "(?<!"; meaning = "start of a negative lookbehind (must NOT be preceded by…)"; }
                    else if (rest.StartsWith("(?>")) { tok = "(?>"; meaning = "start of an atomic group"; }
                    else
                    {
                        var nm = Regex.Match(rest, @"^\(\?(?:<([A-Za-z_]\w*)>|'([A-Za-z_]\w*)')");
                        var fl = Regex.Match(rest, @"^\(\?[imnsx-]+\)");
                        if (nm.Success) { groupNo++; tok = nm.Value; meaning = $"start of capture group #{groupNo} named '{(nm.Groups[1].Success ? nm.Groups[1].Value : nm.Groups[2].Value)}'"; }
                        else if (fl.Success) { tok = fl.Value; meaning = "inline option flags: " + fl.Value[2..^1]; Line(tok, meaning); i += tok.Length; break; }
                        else { groupNo++; tok = "("; meaning = $"start of capture group #{groupNo}"; }
                    }
                    Line(tok, meaning);
                    depth++;
                    i += tok.Length;
                    break;
                }
                case ')':
                    depth = Math.Max(0, depth - 1);
                    Line(")", "end of group");
                    i++;
                    break;
                case '|': Line("|", "OR — either the part before or the part after"); i++; break;
                case '^': Line("^", "start of the text (or of each line with Multiline)"); i++; break;
                case '$': Line("$", "end of the text (or of each line with Multiline)"); i++; break;
                case '.': Line(".", "any character (except a newline, unless Singleline)"); i++; break;
                case '*': case '+': case '?':
                {
                    string tok = c.ToString();
                    string m = c switch { '*' => "zero or more times", '+' => "one or more times", _ => "optional (zero or one time)" };
                    if (i + 1 < p.Length && p[i + 1] == '?' ) { tok += "?"; m += ", as few as possible (lazy)"; }
                    Line(tok, "quantifier: previous item " + m);
                    i += tok.Length;
                    break;
                }
                case '{':
                {
                    var q = Regex.Match(p[i..], @"^\{(\d+)(,(\d*))?\}\??");
                    if (q.Success)
                    {
                        string m = !q.Groups[2].Success ? $"exactly {q.Groups[1].Value} times"
                            : q.Groups[3].Value.Length == 0 ? $"at least {q.Groups[1].Value} times"
                            : $"between {q.Groups[1].Value} and {q.Groups[3].Value} times";
                        if (q.Value.EndsWith("?")) m += ", as few as possible (lazy)";
                        Line(q.Value, "quantifier: previous item " + m);
                        i += q.Length;
                    }
                    else { Line("{", "the literal character '{'"); i++; }
                    break;
                }
                default:
                {
                    int j = i;
                    while (j < p.Length && Specials.IndexOf(p[j]) < 0) j++;
                    if (j < p.Length && "*+?{".IndexOf(p[j]) >= 0 && j - i > 1) j--;   // quantifier applies to last char only
                    var lit = p.Substring(i, j - i);
                    Line(lit, lit.Length == 1 ? $"the literal character '{lit}'" : $"the literal text \"{lit}\"");
                    i = j;
                    break;
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string DescribeSet(string body)
    {
        var parts = new List<string>();
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length)
            {
                char n = body[++i];
                parts.Add(n switch { 'd' => "digits", 'w' => "word characters", 's' => "whitespace", 'D' => "non-digits", 'W' => "non-word characters", 'S' => "non-whitespace", 'n' => "newline", 't' => "tab", _ => $"'{n}'" });
            }
            else if (i + 2 < body.Length && body[i + 1] == '-')
            {
                parts.Add($"{body[i]}–{body[i + 2]}");
                i += 2;
            }
            else parts.Add($"'{body[i]}'");
        }
        return string.Join(", ", parts);
    }
}
