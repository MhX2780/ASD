using System.Globalization;
using System.Text.RegularExpressions;

namespace ASD;

// ─────────────────────────────────────────────────────────────────────────────
//  .automation  -  a small, line-based language for "cells connected by arrows".
//
//    # comment
//    automation: Nightly check
//
//    cell health:
//      title: Check API                 shown big on the card
//      note: GET /health                shown small under the title
//      at: 40 60                        card position in the editor (optional)
//      http: GET https://example.com/health
//      success -> download              green arrow: this cell ended with exit code 0
//      failure -> alert                 red arrow:   this cell ended with exit code other than 0
//      when powershell -c "exit 0" -> deep    orange arrow: runs the command, fires if it exits 0
//      success when test.cmd -> other   a status AND a command together
//      exit 2 -> retry                  fires only when the exit code is exactly 2
//      failure contains timeout -> slow fires only when the cell's output contains "timeout" (any letter case)
//      always exit 3 contains busy when ping -n 1 x -> t     conditions can be combined: all of them must be true
//      otherwise -> d                   fires only when none of the cell's other arrows fired (the "else" branch)
//      success -> next | color #8B5CF6 | name Deploy     after the target: your own arrow color and your own text on the arrow
//
//    cell alert:
//      notify: API is down
//      level: error
//
//  One step per cell:  run: <command>  |  http: [METHOD] <url>  |  notify: <text>  |  wait: <seconds>
//                      |  status: incoming        (takes the result of the cell before it; its arrows decide by that result)
//                      |  upload: <file>  +  to: dropbox /Folder/  |  post <url>  |  put <url>
//  A cell with no step only passes through (use it as a note or a "Finish" marker).
//  A cell with no arrows is where its branch ends.
// ─────────────────────────────────────────────────────────────────────────────

internal enum ArrowKind { Success, Failure, Always }
internal enum StepKind { None, Run, Http, Notify, Wait, Status, Upload }

/// <summary>What a cell waits for when several arrows point at it.</summary>
internal enum JoinMode
{
    /// <summary>Wait until every arrow is decided; run if at least one fired. (default)</summary>
    Settle,
    /// <summary>Wait until every arrow is decided; run only if all of them fired.</summary>
    All,
    /// <summary>Run as soon as the first arrow fires.</summary>
    First,
}

internal sealed class AutoArrow
{
    public ArrowKind Kind = ArrowKind.Success;
    /// <summary>Optional command: the arrow fires only if it exits with 0.</summary>
    public string? When;
    /// <summary>Optional: the arrow fires only when the cell's exit code is exactly this number.</summary>
    public int? ExitCode;
    /// <summary>Optional: the arrow fires only when the cell's output contains this text (letter case ignored).</summary>
    public string? Contains;
    /// <summary>Optional: your own color for the arrow, "#RRGGBB" (the editor also accepts a color name and writes the hex).</summary>
    public string? Color;
    /// <summary>Optional: your own text on the arrow instead of "success" / "failure" / ...</summary>
    public string? Name;
    /// <summary>The "else" arrow: fires only when none of the other arrows of the same cell fired.</summary>
    public bool Otherwise;
    public string To = "";
    public int Line;

    /// <summary>True for a plain green / red / grey arrow: no extra condition, no own color, no own name.</summary>
    public bool IsPlain => !Otherwise && When == null && ExitCode == null && Contains == null && Color == null && Name == null;

    /// <summary>The arrow's condition in the language's own words (what comes before "-&gt;").</summary>
    public string Label
    {
        get
        {
            if (Otherwise) return "otherwise";
            var parts = new List<string>();
            var hasExtra = When != null || ExitCode != null || Contains != null;
            if (Kind != ArrowKind.Always || !hasExtra) parts.Add(Kind switch { ArrowKind.Success => "success", ArrowKind.Failure => "failure", _ => "always" });
            if (ExitCode != null) parts.Add("exit " + ExitCode.Value.ToString(CultureInfo.InvariantCulture));
            if (Contains != null) parts.Add("contains " + Contains);
            if (When != null) parts.Add("when " + When);
            return string.Join(" ", parts);
        }
    }

    /// <summary>"#RRGGBB" -> (r,g,b), or null when the text is not a color. Also accepts #RGB and a few names.</summary>
    public static (byte R, byte G, byte B)? ParseColor(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var named = t.ToLowerInvariant() switch
        {
            "green" => "#22A04B", "red" => "#E5484D", "orange" => "#F59E0B", "blue" => "#3B82F6", "purple" => "#8B5CF6",
            "pink" => "#EC4899", "teal" => "#14B8A6", "yellow" => "#EAB308", "grey" or "gray" => "#8A8A8A", "black" => "#000000", "white" => "#FFFFFF",
            _ => null,
        };
        if (named != null) t = named;
        if (!t.StartsWith('#')) return null;
        var hex = t[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => new string(ch, 2)));
        if (hex.Length != 6) return null;
        return byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
            ? (r, g, b) : null;
    }

    /// <summary>The color as the language writes it: "#RRGGBB" in capitals, or null.</summary>
    public static string? NormalizeColor(string? text)
    {
        var c = ParseColor(text);
        return c == null ? null : $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";
    }
}

internal sealed class AutoCell
{
    public string Id = "";
    public string Title = "";
    public string Note = "";
    public int X = -1, Y = -1;                 // -1 = not placed yet (the editor lays it out)
    public StepKind Step = StepKind.None;
    public string Command = "";                // run: command, notify: text
    public string HttpMethod = "GET";
    public string HttpUrl = "";
    public string Body = "";
    public readonly List<KeyValuePair<string, string>> Headers = new();
    public double WaitSeconds;
    public string Level = "info";              // notify: success | error | warning | info
    public int TimeoutSeconds;                 // 0 = default for the step
    public JoinMode Join = JoinMode.Settle;
    public string UploadFile = "";             // upload: the file to send
    public string UploadTo = "";               // upload: "dropbox /Folder/" | "post <url>" | "put <url>"
    public readonly List<AutoArrow> Out = new();
    public int Line;

    public string Display => Title.Length > 0 ? Title : Id;
}

internal sealed class AutoFile
{
    public string Name = "";
    public readonly List<AutoCell> Cells = new();
    public readonly List<string> Errors = new();
    public readonly List<string> Warnings = new();

    public bool Ok => Errors.Count == 0;

    public AutoCell? Find(string id)
        => Cells.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

internal static class AutomationParser
{
    private static readonly Regex IdRx = new(@"^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex CellRx = new(@"^cell\s+(\S+?)\s*:?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
        { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    private static readonly HashSet<string> Levels = new(StringComparer.OrdinalIgnoreCase)
        { "success", "error", "warning", "info" };

    public static AutoFile Parse(string text)
    {
        var f = new AutoFile();
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        AutoCell? cur = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var ln = i + 1;
            var raw = lines[i].TrimEnd();
            if (ln == 1 && raw.Length > 0 && raw[0] == '﻿') raw = raw[1..];
            var t = raw.TrimStart();
            if (t.Length == 0 || t[0] == '#') continue;
            var indented = raw[0] == ' ' || raw[0] == '\t';

            if (!indented)
            {
                cur = null;
                seen.Clear();
                var colon = t.IndexOf(':');
                if (colon > 0 && t[..colon].Trim().Equals("automation", StringComparison.OrdinalIgnoreCase))
                {
                    f.Name = t[(colon + 1)..].Trim();
                    continue;
                }
                var m = CellRx.Match(t);
                if (!m.Success)
                {
                    f.Errors.Add($"line {ln}: expected \"cell <id>:\" or \"automation: <name>\"");
                    continue;
                }
                var id = m.Groups[1].Value;
                if (!IdRx.IsMatch(id))
                {
                    f.Errors.Add($"line {ln}: cell id \"{id}\" may only use letters, digits, - and _ (max 40)");
                    continue;
                }
                if (f.Find(id) != null)
                {
                    f.Errors.Add($"line {ln}: cell \"{id}\" is defined twice");
                    continue;
                }
                cur = new AutoCell { Id = id, Line = ln };
                f.Cells.Add(cur);
                continue;
            }

            if (cur == null)
            {
                f.Errors.Add($"line {ln}: this line is not inside a cell (start one with \"cell <id>:\")");
                continue;
            }

            if (LooksLikeArrow(t)) { ParseArrow(f, cur, t, ln); continue; }

            var c = t.IndexOf(':');
            if (c <= 0)
            {
                f.Errors.Add($"line {ln}: expected \"name: value\" or an arrow such as \"success -> next\"");
                continue;
            }
            var key = t[..c].Trim().ToLowerInvariant();
            var val = t[(c + 1)..].Trim();
            if (key != "header" && !seen.Add(key))
            {
                f.Errors.Add($"line {ln}: \"{key}\" is set twice in cell \"{cur.Id}\"");
                continue;
            }
            ParseProperty(f, cur, key, val, ln);
        }

        Validate(f);
        return f;
    }

    private static string FirstWord(string s)
    {
        var n = 0;
        while (n < s.Length && !char.IsWhiteSpace(s[n])) n++;
        return s[..n];
    }

    private static bool LooksLikeArrow(string t)
    {
        if (!t.Contains("->", StringComparison.Ordinal)) return false;
        var first = FirstWord(t).ToLowerInvariant();
        return first is "success" or "failure" or "always" or "when" or "exit" or "contains" or "otherwise";
    }

    private static void ParseArrow(AutoFile f, AutoCell cell, string t, int ln)
    {
        var idx = t.LastIndexOf("->", StringComparison.Ordinal);
        var head = t[..idx].Trim();
        var tail = t[(idx + 2)..].Trim();

        // after the target(s): "| color #RRGGBB | name some text"
        string? color = null, name = null;
        var bar = tail.IndexOf('|');
        if (bar >= 0)
        {
            var options = tail[(bar + 1)..].Split('|', StringSplitOptions.TrimEntries);
            tail = tail[..bar].Trim();
            foreach (var opt in options)
            {
                if (opt.Length == 0) continue;
                var key = FirstWord(opt);
                var value = opt[key.Length..].Trim();
                switch (key.ToLowerInvariant())
                {
                    case "color":
                    case "colour":
                        color = AutoArrow.NormalizeColor(value);
                        if (color == null) { f.Errors.Add($"line {ln}: \"{value}\" is not a color (write it like  color #8B5CF6  or a name such as orange, blue, purple)"); return; }
                        break;
                    case "name":
                        if (value.Length == 0) { f.Errors.Add($"line {ln}: \"name\" needs a text, e.g.  name Retry"); return; }
                        name = value;
                        break;
                    default:
                        f.Errors.Add($"line {ln}: unknown arrow option \"{key}\" after the target (use  | color #RRGGBB  and  | name text)");
                        return;
                }
            }
        }

        var first = FirstWord(head);
        var rest = head;
        var kind = ArrowKind.Always;
        var otherwise = false;
        switch (first.ToLowerInvariant())
        {
            case "otherwise": otherwise = true; rest = head[first.Length..].Trim(); break;
            case "success": kind = ArrowKind.Success; rest = head[first.Length..].Trim(); break;
            case "failure": kind = ArrowKind.Failure; rest = head[first.Length..].Trim(); break;
            case "always": kind = ArrowKind.Always; rest = head[first.Length..].Trim(); break;
        }

        // optional conditions, in this order:  exit N   contains TEXT   when COMMAND
        int? exitCode = null;
        string? contains = null, cond = null;
        if (rest.StartsWith("exit ", StringComparison.OrdinalIgnoreCase) || rest.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest.Length > 4 ? rest[5..].Trim() : "";
            var num = FirstWord(rest);
            if (!int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            { f.Errors.Add($"line {ln}: \"exit\" needs a number, e.g.  exit 2 -> retry"); return; }
            exitCode = code;
            rest = rest[num.Length..].Trim();
        }
        if (rest.StartsWith("contains ", StringComparison.OrdinalIgnoreCase) || rest.Equals("contains", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest.Length > 8 ? rest[9..].Trim() : "";
            var w = rest.IndexOf(" when ", StringComparison.OrdinalIgnoreCase);
            var text = (w >= 0 ? rest[..w] : rest).Trim();
            rest = w >= 0 ? rest[w..].Trim() : "";
            if (text.Length == 0) { f.Errors.Add($"line {ln}: \"contains\" needs a text, e.g.  contains timeout -> slow"); return; }
            contains = text;
        }
        if (rest.StartsWith("when ", StringComparison.OrdinalIgnoreCase) || rest.Equals("when", StringComparison.OrdinalIgnoreCase))
        {
            cond = rest.Length > 4 ? rest[5..].Trim() : "";
            if (cond.Length == 0) { f.Errors.Add($"line {ln}: \"when\" needs a command, e.g.  when powershell -c \"exit 0\" -> next"); return; }
            rest = "";
        }
        if (rest.Length > 0)
        {
            var kw = first.Length > 0 ? first : "success";
            f.Errors.Add($"line {ln}: unexpected text \"{rest}\" before the arrow (use \"{kw} exit N contains TEXT when <command> -> target\")");
            return;
        }
        if (first.Equals("when", StringComparison.OrdinalIgnoreCase) && cond == null)
        { f.Errors.Add($"line {ln}: \"when\" needs a command, e.g.  when powershell -c \"exit 0\" -> next"); return; }

        if (otherwise && (cond != null || exitCode != null || contains != null))
        { f.Errors.Add($"line {ln}: \"otherwise\" fires when nothing else did, so it cannot have conditions of its own"); return; }

        var targets = tail.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length == 0) { f.Errors.Add($"line {ln}: the arrow has no target cell after \"->\""); return; }
        foreach (var to in targets)
            cell.Out.Add(new AutoArrow { Kind = kind, Otherwise = otherwise, When = cond, ExitCode = exitCode, Contains = contains, Color = color, Name = name, To = to, Line = ln });
    }

    private static void SetStep(AutoFile f, AutoCell cell, StepKind kind, int ln)
    {
        if (cell.Step != StepKind.None)
        {
            f.Errors.Add($"line {ln}: cell \"{cell.Id}\" already has a step; use only one of run / http / notify / wait / status / upload");
            return;
        }
        cell.Step = kind;
    }

    private static void ParseProperty(AutoFile f, AutoCell cell, string key, string val, int ln)
    {
        switch (key)
        {
            case "title": cell.Title = val; break;
            case "note": cell.Note = val; break;
            case "at":
            {
                var p = val.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 2
                    && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                { cell.X = x; cell.Y = y; }
                else f.Errors.Add($"line {ln}: \"at\" needs two numbers, e.g.  at: 40 60");
                break;
            }
            case "run":
                if (val.Length == 0) { f.Errors.Add($"line {ln}: \"run\" needs a command"); break; }
                SetStep(f, cell, StepKind.Run, ln); cell.Command = val; break;
            case "notify":
                if (val.Length == 0) { f.Errors.Add($"line {ln}: \"notify\" needs a text"); break; }
                SetStep(f, cell, StepKind.Notify, ln); cell.Command = val; break;
            case "http":
            {
                var first = FirstWord(val);
                var method = "GET";
                var url = val;
                if (Methods.Contains(first)) { method = first.ToUpperInvariant(); url = val[first.Length..].Trim(); }
                if (url.Length == 0 || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                        && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                        && !url.Contains("{{", StringComparison.Ordinal)))
                { f.Errors.Add($"line {ln}: \"http\" needs [METHOD] and a URL starting with http:// or https://"); break; }
                SetStep(f, cell, StepKind.Http, ln); cell.HttpMethod = method; cell.HttpUrl = url; break;
            }
            case "status":
                SetStep(f, cell, StepKind.Status, ln); break;      // the text after the colon is only a note ("incoming")
            case "upload":
                if (val.Length == 0) { f.Errors.Add($"line {ln}: \"upload\" needs the path of the file to send"); break; }
                SetStep(f, cell, StepKind.Upload, ln); cell.UploadFile = val; break;
            case "to":
                if (val.Length == 0) { f.Errors.Add($"line {ln}: \"to\" needs a target, e.g.  to: dropbox /Reports/  or  to: post https://example.com/upload"); break; }
                cell.UploadTo = val; break;
            case "wait":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs >= 0 && secs <= 86400)
                { SetStep(f, cell, StepKind.Wait, ln); cell.WaitSeconds = secs; }
                else f.Errors.Add($"line {ln}: \"wait\" needs a number of seconds (0 to 86400)");
                break;
            case "body": cell.Body = val; break;
            case "header":
            {
                var c = val.IndexOf(':');
                if (c <= 0) { f.Errors.Add($"line {ln}: \"header\" needs \"Name: value\""); break; }
                cell.Headers.Add(new KeyValuePair<string, string>(val[..c].Trim(), val[(c + 1)..].Trim()));
                break;
            }
            case "level":
                if (Levels.Contains(val)) cell.Level = val.ToLowerInvariant();
                else f.Errors.Add($"line {ln}: \"level\" must be success, error, warning or info");
                break;
            case "timeout":
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) && to >= 0 && to <= 86400)
                    cell.TimeoutSeconds = to;
                else f.Errors.Add($"line {ln}: \"timeout\" needs a number of seconds (0 = default)");
                break;
            case "join":
                switch (val.ToLowerInvariant())
                {
                    case "settle": cell.Join = JoinMode.Settle; break;
                    case "all": cell.Join = JoinMode.All; break;
                    case "first": cell.Join = JoinMode.First; break;
                    default: f.Errors.Add($"line {ln}: \"join\" must be settle, all or first"); break;
                }
                break;
            default:
                f.Errors.Add($"line {ln}: unknown setting \"{key}\" (known: title, note, at, run, http, notify, wait, status, upload, to, body, header, level, timeout, join)");
                break;
        }
    }

    private static void Validate(AutoFile f)
    {
        if (f.Cells.Count == 0 && f.Errors.Count == 0)
            f.Errors.Add("the file has no cells (write \"cell <id>:\")");

        foreach (var c in f.Cells)
        {
            var dup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in c.Out)
            {
                if (f.Find(a.To) == null)
                    f.Errors.Add($"line {a.Line}: cell \"{c.Id}\" points to \"{a.To}\", which does not exist");
                else if (a.To.Equals(c.Id, StringComparison.OrdinalIgnoreCase))
                    f.Errors.Add($"line {a.Line}: cell \"{c.Id}\" points to itself (loops are not supported yet)");
                else if (!dup.Add(a.Label + "->" + a.To))
                    f.Warnings.Add($"line {a.Line}: the same arrow \"{a.Label} -> {a.To}\" is written twice");
            }
            if (c.Step != StepKind.Notify && c.Level != "info") f.Warnings.Add($"line {c.Line}: \"level\" only affects notify cells");
            if (c.Step == StepKind.Upload)
            {
                var kind = FirstWord(c.UploadTo).ToLowerInvariant();
                var rest = c.UploadTo[FirstWord(c.UploadTo).Length..].Trim();
                if (c.UploadTo.Length == 0) f.Errors.Add($"line {c.Line}: cell \"{c.Id}\" uploads a file but has no \"to:\" (e.g.  to: dropbox /Reports/)");
                else if (kind is not ("dropbox" or "post" or "put") || rest.Length == 0)
                    f.Errors.Add($"line {c.Line}: cell \"{c.Id}\": \"to\" must be  dropbox <path>,  post <url>  or  put <url>");
                else if (kind != "dropbox" && !rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !rest.Contains("{{", StringComparison.Ordinal))
                    f.Errors.Add($"line {c.Line}: cell \"{c.Id}\": the {kind} URL must start with http:// or https://");
            }
            else if (c.UploadTo.Length > 0) f.Warnings.Add($"line {c.Line}: \"to\" only affects upload cells");
            if (c.Step == StepKind.Status && !f.Cells.Any(o => o.Out.Any(a => a.To.Equals(c.Id, StringComparison.OrdinalIgnoreCase))))
                f.Warnings.Add($"line {c.Line}: status cell \"{c.Id}\" has no arrow pointing at it, so there is no result to read (it will report exit 0)");
        }

        if (f.Errors.Count > 0) return;

        // cycle check (Kahn)
        var indeg = f.Cells.ToDictionary(c => c.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var c in f.Cells)
            foreach (var a in c.Out) indeg[f.Find(a.To)!.Id]++;
        var queue = new Queue<AutoCell>(f.Cells.Where(c => indeg[c.Id] == 0));
        var visited = 0;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            visited++;
            foreach (var a in c.Out)
            {
                var to = f.Find(a.To)!;
                if (--indeg[to.Id] == 0) queue.Enqueue(to);
            }
        }
        if (visited < f.Cells.Count)
        {
            var stuck = f.Cells.Where(c => indeg[c.Id] > 0).Select(c => c.Id);
            f.Errors.Add("the arrows form a loop between: " + string.Join(", ", stuck) + " (loops are not supported yet)");
        }
    }
}
