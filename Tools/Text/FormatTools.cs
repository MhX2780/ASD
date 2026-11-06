using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ASD;

public static class FormatTools
{
    // ───────────── XML ─────────────
    public static string Xml(string xml, int indent)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = new string(' ', indent),
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.Replace,
        };
        var sb = new StringBuilder();
        if (doc.Declaration != null) sb.AppendLine(doc.Declaration.ToString());
        using (var xw = XmlWriter.Create(sb, settings)) doc.Save(xw);
        return sb.ToString().Trim();
    }

    public static string XmlMinify(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var body = doc.ToString(SaveOptions.DisableFormatting);
        return doc.Declaration != null ? doc.Declaration + body : body;
    }

    // ───────────── HTML ─────────────
    private static readonly Regex HtmlTok = new(
        @"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<![^>]*>|<\?.*?\?>|</?[A-Za-z][^>]*>|[^<]+|<", RegexOptions.Singleline);

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    private static readonly HashSet<string> RawTags = new(StringComparer.OrdinalIgnoreCase)
    { "script", "style", "pre", "textarea" };

    public static string Html(string html, int indent)
    {
        var sb = new StringBuilder();
        int depth = 0;
        var toks = HtmlTok.Matches(html).Select(m => m.Value).ToList();

        void Line(string s) => sb.Append(new string(' ', depth * indent)).Append(s).Append('\n');

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.StartsWith("</"))
            {
                depth = Math.Max(0, depth - 1);
                Line(t);
            }
            else if (t.StartsWith("<!") || t.StartsWith("<?")) Line(t);
            else if (t.Length > 1 && t[0] == '<' && char.IsLetter(t[1]))
            {
                var name = Regex.Match(t, @"^<([A-Za-z][\w:-]*)").Groups[1].Value;
                bool selfClose = t.EndsWith("/>") || VoidTags.Contains(name);
                if (RawTags.Contains(name) && !selfClose)
                {
                    int j = i + 1;
                    var inner = new StringBuilder();
                    while (j < toks.Count && !toks[j].StartsWith("</" + name, StringComparison.OrdinalIgnoreCase)) { inner.Append(toks[j]); j++; }
                    var close = j < toks.Count ? toks[j] : "";
                    Line(t + inner + close);
                    i = j;
                }
                else
                {
                    Line(t);
                    if (!selfClose) depth++;
                }
            }
            else
            {
                var txt = Regex.Replace(t, @"\s+", " ").Trim();
                if (txt.Length > 0) Line(txt);
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string HtmlMinify(string html)
    {
        html = Regex.Replace(html, @"<!--(?!\[if).*?-->", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @">\s+<", "><");
        return Regex.Replace(html, @"\s{2,}", " ").Trim();
    }

    // ───────────── SQL ─────────────
    private static readonly Regex SqlTok = new(
        "'(?:''|[^'])*'" + "|" + "\"(?:\"\"|[^\"])*\"" + "|" + "`[^`]*`" + "|" + @"--[^\n]*" + "|" + @"/\*.*?\*/" + "|" +
        @"\[[^\]]*\]" + "|" + @"[\p{L}_][\w$#@]*" + "|" + @"\d+(?:\.\d+)?" + "|" + @"<>|<=|>=|!=|\|\||::" + "|" + @"\S",
        RegexOptions.Singleline);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","AND","OR","NOT","IN","IS","NULL","AS","ON","JOIN","LEFT","RIGHT","FULL","INNER","OUTER","CROSS",
        "GROUP","ORDER","BY","HAVING","LIMIT","OFFSET","DISTINCT","UNION","ALL","INSERT","INTO","VALUES","UPDATE","SET","DELETE",
        "CREATE","TABLE","ALTER","DROP","INDEX","PRIMARY","KEY","FOREIGN","REFERENCES","DEFAULT","CASE","WHEN","THEN","ELSE","END",
        "BETWEEN","LIKE","EXISTS","ASC","DESC","WITH","TOP","INTERSECT","EXCEPT","NATURAL","USING","OVER","PARTITION",
    };

    private static readonly HashSet<string> ClauseStarts = new()
    {
        "SELECT","FROM","WHERE","GROUP BY","ORDER BY","HAVING","LIMIT","OFFSET","UNION","UNION ALL","INTERSECT","EXCEPT",
        "INSERT INTO","VALUES","UPDATE","SET","DELETE FROM",
    };

    private static readonly HashSet<string> JoinWords = new() { "LEFT", "RIGHT", "FULL", "INNER", "CROSS", "OUTER", "NATURAL", "JOIN" };

    private static List<string> SqlTokens(string sql)
        => SqlTok.Matches(sql).Select(m => m.Value).ToList();

    private static List<string> MergeSql(List<string> toks)
    {
        var merged = new List<string>();
        for (int i = 0; i < toks.Count; i++)
        {
            var u = toks[i].ToUpperInvariant();
            string? next = i + 1 < toks.Count ? toks[i + 1].ToUpperInvariant() : null;
            if ((u == "GROUP" || u == "ORDER") && next == "BY") { merged.Add(u + " BY"); i++; }
            else if (u == "INSERT" && next == "INTO") { merged.Add("INSERT INTO"); i++; }
            else if (u == "DELETE" && next == "FROM") { merged.Add("DELETE FROM"); i++; }
            else if (u == "UNION" && next == "ALL") { merged.Add("UNION ALL"); i++; }
            else if (JoinWords.Contains(u))
            {
                var words = new List<string>();
                int j = i;
                while (j < toks.Count && JoinWords.Contains(toks[j].ToUpperInvariant()))
                {
                    var w = toks[j].ToUpperInvariant();
                    words.Add(w);
                    j++;
                    if (w == "JOIN") break;
                }
                merged.Add(string.Join(" ", words));
                i = j - 1;
            }
            else merged.Add(toks[i]);
        }
        return merged;
    }

    private static bool IsComment(string t) => t.StartsWith("--") || t.StartsWith("/*");

    private static bool NeedSpace(string? prev, string t)
    {
        if (prev == null) return false;
        if (t is "," or ")" or "." or ";") return false;
        if (prev is "(" or ".") return false;
        if (t == "(") return Keywords.Contains(prev) || prev == ")" || prev == ",";
        return true;
    }

    public static string Sql(string sql, int indent)
    {
        var toks = MergeSql(SqlTokens(sql));
        var sb = new StringBuilder();
        var stack = new Stack<bool>();   // true = sub-query parenthesis
        int level = 0;
        string clause = "";
        string? prev = null;
        bool lineStart = true, between = false;

        void NewLine(int lvl)
        {
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(new string(' ', Math.Max(0, lvl) * indent));
            lineStart = true;
            prev = null;
        }

        void Emit(string text)
        {
            if (!lineStart && NeedSpace(prev, text)) sb.Append(' ');
            sb.Append(text);
            lineStart = false;
            prev = text;
        }

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            var u = t.ToUpperInvariant();

            if (IsComment(t))
            {
                Emit(t);
                if (t.StartsWith("--")) NewLine(level);
                continue;
            }
            if (ClauseStarts.Contains(u))
            {
                if (sb.Length > 0 && !(prev == "(")) NewLine(level);
                Emit(u);
                clause = u;
                continue;
            }
            if (u.EndsWith("JOIN"))
            {
                NewLine(level);
                Emit(u);
                clause = "JOIN";
                continue;
            }
            if (u == "BETWEEN") { between = true; Emit(u); continue; }
            if ((u == "AND" || u == "OR") && (clause is "WHERE" or "HAVING" or "JOIN"))
            {
                if (u == "AND" && between) { between = false; Emit(u); continue; }
                NewLine(level + 1);
                Emit(u);
                continue;
            }
            if (t == ",")
            {
                Emit(",");
                bool topLevel = stack.Count == 0 || stack.Peek();
                if (topLevel && clause is "SELECT" or "GROUP BY" or "ORDER BY" or "SET") NewLine(level + 1);
                continue;
            }
            if (t == "(")
            {
                bool sub = i + 1 < toks.Count && toks[i + 1].Equals("SELECT", StringComparison.OrdinalIgnoreCase);
                Emit("(");
                stack.Push(sub);
                if (sub) level++;
                lineStart = true;    // no space after "("
                prev = "(";
                continue;
            }
            if (t == ")")
            {
                bool sub = stack.Count > 0 && stack.Pop();
                if (sub) { level--; NewLine(level); }
                Emit(")");
                continue;
            }
            if (t == ";")
            {
                Emit(";");
                sb.Append("\n\n");
                level = 0; clause = ""; lineStart = true; prev = null; stack.Clear();
                continue;
            }
            Emit(Keywords.Contains(t) ? u : t);
        }
        return sb.ToString().Trim();
    }

    public static string SqlMinify(string sql)
    {
        var sb = new StringBuilder();
        string? prev = null;
        foreach (var t in MergeSql(SqlTokens(sql)))
        {
            if (IsComment(t)) continue;
            var text = Keywords.Contains(t) ? t.ToUpperInvariant() : t;
            if (NeedSpace(prev, text)) sb.Append(' ');
            sb.Append(text);
            prev = text;
        }
        return sb.ToString();
    }
}
