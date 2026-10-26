using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ASD;

public static class JsonTools
{
    private static readonly JsonSerializerOptions PrettyOpts = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions CompactOpts = new()
    { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions DocOpts = new()
    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    // ───────────── basics ─────────────
    public static JsonNode? Parse(string text) => JsonNode.Parse(text, null, DocOpts);

    public static string Format(JsonNode? node, string indent = "  ")
    {
        var s = node == null ? "null" : node.ToJsonString(PrettyOpts);
        if (indent == "  ") return s;
        var lines = s.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            int n = 0;
            while (n < l.Length && l[n] == ' ') n++;
            lines[i] = string.Concat(Enumerable.Repeat(indent, n / 2)) + l[n..];
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string Minify(JsonNode? node) => node == null ? "null" : node.ToJsonString(CompactOpts);

    public static string Quote(string s) => JsonSerializer.Serialize(s, CompactOpts);

    public static string DescribeError(Exception ex)
    {
        if (ex is JsonException je && je.LineNumber.HasValue)
            return $"Invalid JSON — line {je.LineNumber + 1}, column {(je.BytePositionInLine ?? 0) + 1}: {je.Message.Split(" Path:")[0]}";
        return "Error: " + ex.Message;
    }

    public static JsonNode? SortKeys(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                var res = new JsonObject();
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                    res.Add(kv.Key, SortKeys(kv.Value));
                return res;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var x in a) arr.Add(SortKeys(x));
                return arr;
            default:
                return n?.DeepClone();
        }
    }

    // ───────────── JSON → YAML ─────────────
    public static string ToYaml(JsonNode? node)
    {
        var sb = new StringBuilder();
        EmitYaml(node, sb, 0);
        return sb.ToString().TrimEnd();
    }

    private static bool IsInline(JsonNode? n)
        => n is null || (n is JsonObject o ? o.Count == 0 : n is JsonArray a ? a.Count == 0 : true);

    private static string InlineYaml(JsonNode? n)
    {
        if (n is null) return "null";
        if (n is JsonObject) return "{}";
        if (n is JsonArray) return "[]";
        return n.GetValueKind() switch
        {
            JsonValueKind.String => YamlString(n.GetValue<string>()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => n.ToJsonString(),
        };
    }

    private static string YamlString(string s)
    {
        bool needsQuote = s.Length == 0
            || s != s.Trim()
            || s.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', '\n', '\r', '\t' }) >= 0
            || s.StartsWith("- ") || s.StartsWith("? ")
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || s.ToLowerInvariant() is "true" or "false" or "null" or "yes" or "no" or "on" or "off" or "~";
        return needsQuote ? Quote(s) : s;
    }

    private static void EmitYaml(JsonNode? n, StringBuilder sb, int ind)
    {
        var pad = new string(' ', ind);
        if (n is JsonObject o && o.Count > 0)
        {
            foreach (var kv in o)
            {
                var key = YamlString(kv.Key);
                if (IsInline(kv.Value)) sb.Append(pad).Append(key).Append(": ").AppendLine(InlineYaml(kv.Value));
                else { sb.Append(pad).Append(key).AppendLine(":"); EmitYaml(kv.Value, sb, ind + 2); }
            }
        }
        else if (n is JsonArray a && a.Count > 0)
        {
            foreach (var item in a)
            {
                if (IsInline(item)) sb.Append(pad).Append("- ").AppendLine(InlineYaml(item));
                else
                {
                    var sub = new StringBuilder();
                    EmitYaml(item, sub, ind + 2);
                    sb.Append(pad).Append("- ").Append(sub.ToString().Substring(ind + 2));
                }
            }
        }
        else sb.Append(pad).AppendLine(InlineYaml(n));
    }

    // ───────────── YAML → JSON (common subset) ─────────────
    private sealed record YL(int Indent, string Text);

    public static JsonNode? FromYaml(string yaml)
    {
        var lines = new List<YL>();
        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            var line = StripComment(raw.Replace("\t", "  "));
            var t = line.Trim();
            if (t.Length == 0 || t == "---" || t == "...") continue;
            lines.Add(new YL(line.Length - line.TrimStart().Length, t));
        }
        if (lines.Count == 0) return null;
        int i = 0;
        return ParseBlock(lines, ref i, lines[0].Indent);
    }

    private static string StripComment(string s)
    {
        char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q == '\0')
            {
                bool boundary = i == 0 || " \t:-[,{".IndexOf(s[i - 1]) >= 0;
                if ((c == '"' || c == '\'') && boundary) q = c;
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1]))) return s[..i].TrimEnd();
            }
            else if (c == q) q = '\0';
        }
        return s;
    }

    private static int FindColon(string t)
    {
        if (t.Length == 0 || t[0] == '[' || t[0] == '{') return -1;
        char q = '\0';
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if ((c == '"' || c == '\'') && i == 0) { q = c; continue; }
            if (c == ':' && (i + 1 == t.Length || t[i + 1] == ' ')) return i;
        }
        return -1;
    }

    private static bool IsSeqLine(string t) => t == "-" || t.StartsWith("- ");

    private static JsonNode? ParseBlock(List<YL> ls, ref int i, int indent)
    {
        if (i >= ls.Count) return null;
        var t = ls[i].Text;
        if (IsSeqLine(t)) return ParseSeq(ls, ref i, indent);
        if (FindColon(t) >= 0) return ParseMap(ls, ref i, indent);
        i++;
        return ParseInline(t);
    }

    private static JsonObject ParseMap(List<YL> ls, ref int i, int indent)
    {
        var obj = new JsonObject();
        while (i < ls.Count && ls[i].Indent == indent)
        {
            var t = ls[i].Text;
            if (IsSeqLine(t)) break;
            int c = FindColon(t);
            if (c < 0) break;
            var key = Unquote(t[..c].Trim());
            var rest = t[(c + 1)..].Trim();
            i++;
            JsonNode? val;
            if (rest.Length == 0)
            {
                if (i < ls.Count && ls[i].Indent > indent) val = ParseBlock(ls, ref i, ls[i].Indent);
                else if (i < ls.Count && ls[i].Indent == indent && IsSeqLine(ls[i].Text)) val = ParseSeq(ls, ref i, indent);
                else val = null;
            }
            else if (rest is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var parts = new List<string>();
                while (i < ls.Count && ls[i].Indent > indent) { parts.Add(ls[i].Text); i++; }
                val = JsonValue.Create(string.Join(rest[0] == '|' ? "\n" : " ", parts));
            }
            else val = ParseInline(rest);
            obj[key] = val;
        }
        return obj;
    }

    private static JsonArray ParseSeq(List<YL> ls, ref int i, int indent)
    {
        var arr = new JsonArray();
        while (i < ls.Count && ls[i].Indent == indent && IsSeqLine(ls[i].Text))
        {
            var t = ls[i].Text;
            var rest = t.Length > 1 ? t[1..].TrimStart() : "";
            int offset = t.Length - rest.Length;
            if (rest.Length == 0)
            {
                i++;
                if (i < ls.Count && ls[i].Indent > indent) arr.Add(ParseBlock(ls, ref i, ls[i].Indent));
                else arr.Add(null);
            }
            else if (IsSeqLine(rest) || FindColon(rest) >= 0)
            {
                ls[i] = new YL(indent + offset, rest);
                arr.Add(ParseBlock(ls, ref i, indent + offset));
            }
            else
            {
                i++;
                arr.Add(ParseInline(rest));
            }
        }
        return arr;
    }

    private static JsonNode? ParseInline(string s)
    {
        s = s.Trim();
        if ((s.StartsWith("[") && s.EndsWith("]")) || (s.StartsWith("{") && s.EndsWith("}")))
        {
            try { return JsonNode.Parse(s); } catch { return JsonValue.Create(s); }
        }
        return ParseScalar(s);
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            if (s[0] == '"') { try { return JsonSerializer.Deserialize<string>(s) ?? ""; } catch { } }
            return s[1..^1].Replace("''", "'");
        }
        return s;
    }

    private static JsonNode? ParseScalar(string s)
    {
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'')) return JsonValue.Create(Unquote(s));
        switch (s.ToLowerInvariant())
        {
            case "null": case "~": case "": return null;
            case "true": return JsonValue.Create(true);
            case "false": return JsonValue.Create(false);
        }
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(s);
    }

    // ───────────── JSON ↔ CSV ─────────────
    private static string CsvCell(JsonNode? v)
    {
        string s;
        if (v is null) s = "";
        else
        {
            var kind = v.GetValueKind();
            s = kind switch
            {
                JsonValueKind.String => v.GetValue<string>(),
                JsonValueKind.Object or JsonValueKind.Array => Minify(v),
                JsonValueKind.Null => "",
                _ => v.ToJsonString(),
            };
        }
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string ToCsv(JsonNode? node)
    {
        JsonArray arr;
        if (node is JsonArray a) arr = a;
        else if (node is JsonObject o0) { arr = new JsonArray(); arr.Add(o0.DeepClone()); }
        else throw new InvalidOperationException("CSV conversion needs a JSON array of objects.");

        var cols = new List<string>();
        var seen = new HashSet<string>();
        bool hasPlain = false;
        foreach (var item in arr)
        {
            if (item is JsonObject o)
            {
                foreach (var kv in o) if (seen.Add(kv.Key)) cols.Add(kv.Key);
            }
            else hasPlain = true;
        }
        if (hasPlain && seen.Add("value")) cols.Add("value");

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(c => CsvCell(JsonValue.Create(c)))));
        foreach (var item in arr)
        {
            var cells = cols.Select(c =>
            {
                if (item is JsonObject o) return CsvCell(o[c]);
                return c == "value" ? CsvCell(item) : "";
            });
            sb.AppendLine(string.Join(",", cells));
        }
        return sb.ToString().TrimEnd();
    }

    public static JsonNode FromCsv(string csv, bool infer)
    {
        var first = csv.Split('\n')[0];
        char d = new[] { ',', ';', '\t' }.OrderByDescending(c => first.Count(x => x == c)).First();
        var rows = ParseCsv(csv, d);
        rows.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
        var result = new JsonArray();
        if (rows.Count == 0) return result;
        var header = rows[0];
        for (int r = 1; r < rows.Count; r++)
        {
            var obj = new JsonObject();
            for (int c = 0; c < header.Count; c++)
            {
                var cell = c < rows[r].Count ? rows[r][c] : "";
                obj[header[c]] = infer ? Infer(cell) : JsonValue.Create(cell);
            }
            result.Add(obj);
        }
        return result;
    }

    private static JsonNode? Infer(string s)
    {
        var t = s.Trim();
        if (t == "true") return JsonValue.Create(true);
        if (t == "false") return JsonValue.Create(false);
        if (t == "null") return null;
        bool leadingZero = t.Length > 1 && t[0] == '0' && t[1] != '.';
        if (!leadingZero && !t.StartsWith("+"))
        {
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
            if (t.Contains('.') && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)) return JsonValue.Create(db);
        }
        return JsonValue.Create(s);
    }

    private static List<List<string>> ParseCsv(string s, char d)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q)
            {
                if (c == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
                    else q = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == d) { row.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(sb.ToString()); sb.Clear(); rows.Add(row); row = new List<string>(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }

    // ───────────── JSONPath (common subset) ─────────────
    // $  .key  ['key']  [n]  [-n]  [a:b]  [*]  .*  ..key  [?(@.price < 10)]
    public static List<JsonNode?> Query(JsonNode? root, string path)
    {
        path = path.Trim();
        var cur = new List<JsonNode?> { root };
        int p = path.StartsWith("$") ? 1 : 0;
        while (p < path.Length)
        {
            bool recursive = false;
            if (path[p] == '.' && p + 1 < path.Length && path[p + 1] == '.') { recursive = true; p += 2; }
            else if (path[p] == '.') p++;

            string seg;
            bool bracket = false;
            if (p < path.Length && path[p] == '[')
            {
                int end = FindBracketEnd(path, p);
                seg = path.Substring(p + 1, end - p - 1).Trim();
                p = end + 1;
                bracket = true;
            }
            else
            {
                int start = p;
                while (p < path.Length && path[p] != '.' && path[p] != '[') p++;
                seg = path.Substring(start, p - start);
                if (seg.Length == 0) throw new FormatException($"Invalid JSONPath near position {p}.");
            }

            var next = new List<JsonNode?>();
            foreach (var n in cur)
            {
                IEnumerable<JsonNode?> scope = recursive ? Descendants(n) : new JsonNode?[] { n };
                foreach (var s in scope) Apply(s, seg, bracket, next);
            }
            cur = next;
        }
        return cur;
    }

    private static int FindBracketEnd(string s, int start)
    {
        char q = '\0';
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c == '"' || c == '\'') q = c;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }
        throw new FormatException("Missing ']' in JSONPath.");
    }

    private static IEnumerable<JsonNode?> Descendants(JsonNode? n)
    {
        yield return n;
        if (n is JsonObject o)
        {
            foreach (var kv in o)
                foreach (var d in Descendants(kv.Value)) yield return d;
        }
        else if (n is JsonArray a)
        {
            foreach (var x in a)
                foreach (var d in Descendants(x)) yield return d;
        }
    }

    private static IEnumerable<JsonNode?> Children(JsonNode? n)
    {
        if (n is JsonObject o) return o.Select(kv => kv.Value);
        if (n is JsonArray a) return a.ToList();
        return Array.Empty<JsonNode?>();
    }

    private static void Apply(JsonNode? node, string seg, bool bracket, List<JsonNode?> next)
    {
        if (seg == "*") { next.AddRange(Children(node)); return; }

        if (bracket && seg.StartsWith("?"))
        {
            foreach (var c in Children(node)) if (FilterMatch(c, seg)) next.Add(c);
            return;
        }
        if (bracket && seg.Length >= 2 && (seg[0] == '\'' || seg[0] == '"'))
        {
            var key = seg[1..^1];
            if (node is JsonObject o && o.TryGetPropertyValue(key, out var v)) next.Add(v);
            return;
        }
        if (bracket && seg.Contains(':') && node is JsonArray arr)
        {
            var parts = seg.Split(':');
            int count = arr.Count;
            int start = parts[0].Length == 0 ? 0 : int.Parse(parts[0]);
            int end = parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1]) : count;
            if (start < 0) start += count;
            if (end < 0) end += count;
            start = Math.Clamp(start, 0, count);
            end = Math.Clamp(end, 0, count);
            for (int i = start; i < end; i++) next.Add(arr[i]);
            return;
        }
        if (bracket && int.TryParse(seg, out var idx))
        {
            if (node is JsonArray a)
            {
                if (idx < 0) idx += a.Count;
                if (idx >= 0 && idx < a.Count) next.Add(a[idx]);
            }
            return;
        }
        if (node is JsonObject obj && obj.TryGetPropertyValue(seg, out var val)) next.Add(val);
    }

    private static readonly Regex FilterRx = new(
        @"^\?\(\s*@((?:\.[A-Za-z0-9_$-]+)*)\s*(==|!=|>=|<=|>|<)?\s*(.*?)\s*\)$", RegexOptions.Singleline);

    private static bool FilterMatch(JsonNode? item, string seg)
    {
        var m = FilterRx.Match(seg);
        if (!m.Success) throw new FormatException("Unsupported filter. Use e.g. [?(@.price < 10)] or [?(@.name == 'x')].");
        JsonNode? cur = item;
        foreach (var key in m.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries))
            cur = (cur as JsonObject)?[key];
        var op = m.Groups[2].Value;
        if (op.Length == 0) return cur != null;
        return Compare(cur, op, m.Groups[3].Value.Trim());
    }

    private static bool Compare(JsonNode? v, string op, string lit)
    {
        if (v == null) return (op == "==" && lit == "null") || (op == "!=" && lit != "null");
        var kind = v.GetValueKind();
        int cmp;
        if (kind == JsonValueKind.Number && double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            cmp = v.GetValue<double>().CompareTo(d);
        else
        {
            var sv = kind == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
            var sl = lit.Length >= 2 && (lit[0] == '\'' || lit[0] == '"') ? lit[1..^1] : lit;
            cmp = string.CompareOrdinal(sv, sl);
        }
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            _ => false,
        };
    }
}
