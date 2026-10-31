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
//
//    cell alert:
//      notify: API is down
//      level: error
//
//  One step per cell:  run: <command>  |  http: [METHOD] <url>  |  notify: <text>  |  wait: <seconds>
//  A cell with no step only passes through (use it as a note or a "Finish" marker).
//  A cell with no arrows is where its branch ends.
// ─────────────────────────────────────────────────────────────────────────────

internal enum ArrowKind { Success, Failure, Always }
internal enum StepKind { None, Run, Http, Notify, Wait }

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
    public string To = "";
    public int Line;

    public string Label => (Kind switch { ArrowKind.Success => "success", ArrowKind.Failure => "failure", _ => When == null ? "always" : "" })
                           + (When != null ? (Kind == ArrowKind.Always ? "when " : " when ") + When : "");
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
        return first is "success" or "failure" or "always" or "when";
    }

    private static void ParseArrow(AutoFile f, AutoCell cell, string t, int ln)
    {
        var idx = t.LastIndexOf("->", StringComparison.Ordinal);
        var head = t[..idx].Trim();
        var tail = t[(idx + 2)..].Trim();
        var first = FirstWord(head);
        var rest = head[first.Length..].Trim();

        var kind = ArrowKind.Success;
        string? cond = null;
        switch (first.ToLowerInvariant())
        {
            case "success": kind = ArrowKind.Success; break;
            case "failure": kind = ArrowKind.Failure; break;
            case "always": kind = ArrowKind.Always; break;
            default: // "when"
                kind = ArrowKind.Always;
                cond = rest;
                rest = "";
                if (cond.Length == 0) { f.Errors.Add($"line {ln}: \"when\" needs a command, e.g.  when powershell -c \"exit 0\" -> next"); return; }
                break;
        }

        if (rest.Length > 0)
        {
            if (rest.StartsWith("when ", StringComparison.OrdinalIgnoreCase))
            {
                cond = rest[5..].Trim();
                if (cond.Length == 0) { f.Errors.Add($"line {ln}: \"when\" needs a command"); return; }
            }
            else
            {
                f.Errors.Add($"line {ln}: unexpected text \"{rest}\" before the arrow (use \"{first} when <command> -> target\")");
                return;
            }
        }

        var targets = tail.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length == 0) { f.Errors.Add($"line {ln}: the arrow has no target cell after \"->\""); return; }
        foreach (var to in targets)
            cell.Out.Add(new AutoArrow { Kind = kind, When = cond, To = to, Line = ln });
    }

    private static void SetStep(AutoFile f, AutoCell cell, StepKind kind, int ln)
    {
        if (cell.Step != StepKind.None)
        {
            f.Errors.Add($"line {ln}: cell \"{cell.Id}\" already has a step; use only one of run / http / notify / wait");
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
                f.Errors.Add($"line {ln}: unknown setting \"{key}\" (known: title, note, at, run, http, notify, wait, body, header, level, timeout, join)");
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
