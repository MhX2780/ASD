namespace ASD;

public sealed class CronSpec
{
    public SortedSet<int> Sec = new() { 0 };
    public SortedSet<int> Min = new(), Hour = new(), Dom = new(), Mon = new(), Dow = new();
    public bool DomStar, DowStar, HasSeconds;
    public string[] Fields = Array.Empty<string>();
}

public static class CronTools
{
    private static readonly string[] MonthNames = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
    private static readonly string[] DowNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
    private static readonly string[] MonthFull = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    private static readonly string[] DowFull = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public static CronSpec Parse(string expr)
    {
        expr = expr.Trim();
        var macro = expr.ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            _ => null,
        };
        if (macro != null) expr = macro;

        var f = expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5 && f.Length != 6)
            throw new FormatException("Expected 5 fields (minute hour day-of-month month weekday) or 6 with a leading seconds field.");

        var spec = new CronSpec { Fields = f };
        int o = 0;
        if (f.Length == 6) { spec.HasSeconds = true; spec.Sec = ParseField(f[0], 0, 59, null, 0); o = 1; }
        spec.Min = ParseField(f[o], 0, 59, null, 0);
        spec.Hour = ParseField(f[o + 1], 0, 23, null, 0);
        spec.Dom = ParseField(f[o + 2], 1, 31, null, 0);
        spec.Mon = ParseField(f[o + 3], 1, 12, MonthNames, 1);
        spec.Dow = new SortedSet<int>(ParseField(f[o + 4], 0, 7, DowNames, 0).Select(x => x % 7));
        spec.DomStar = IsStar(f[o + 2]);
        spec.DowStar = IsStar(f[o + 4]);
        return spec;
    }

    private static bool IsStar(string f) => f.StartsWith("*") || f == "?";

    private static SortedSet<int> ParseField(string field, int min, int max, string[]? names, int nameBase)
    {
        int Val(string s)
        {
            if (names != null)
            {
                int idx = Array.IndexOf(names, s.ToUpperInvariant());
                if (idx >= 0) return idx + nameBase;
            }
            if (!int.TryParse(s, out var v)) throw new FormatException($"'{s}' is not a valid value (field '{field}').");
            return v;
        }

        var set = new SortedSet<int>();
        foreach (var part in field.Split(','))
        {
            var range = part;
            int step = 1;
            bool hasStep = false;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                step = int.Parse(part[(slash + 1)..]);
                hasStep = true;
            }
            int lo, hi;
            if (range is "*" or "?") { lo = min; hi = max; }
            else if (range.Contains('-')) { var ab = range.Split('-'); lo = Val(ab[0]); hi = Val(ab[1]); }
            else { lo = Val(range); hi = hasStep ? max : lo; }

            if (step <= 0 || lo < min || hi > max || lo > hi)
                throw new FormatException($"Value out of range in '{part}' (allowed {min}-{max}).");
            for (int v = lo; v <= hi; v += step) set.Add(v);
        }
        return set;
    }

    // ───────── next run times ─────────
    public static List<DateTime> NextRuns(CronSpec s, DateTime from, int count)
    {
        var res = new List<DateTime>();
        var day = from.Date;
        for (int d = 0; d < 366 * 8 && res.Count < count; d++, day = day.AddDays(1))
        {
            if (!s.Mon.Contains(day.Month) || !DayOk(s, day)) continue;
            foreach (var h in s.Hour)
                foreach (var m in s.Min)
                    foreach (var sec in s.Sec)
                    {
                        var t = new DateTime(day.Year, day.Month, day.Day, h, m, sec);
                        if (t > from) { res.Add(t); if (res.Count >= count) return res; }
                    }
        }
        return res;
    }

    private static bool DayOk(CronSpec s, DateTime d)
    {
        bool dom = s.Dom.Contains(d.Day), dow = s.Dow.Contains((int)d.DayOfWeek);
        if (!s.DomStar && !s.DowStar) return dom || dow;
        if (!s.DomStar) return dom;
        if (!s.DowStar) return dow;
        return true;
    }

    // ───────── description ─────────
    public static string Describe(CronSpec s)
    {
        var f = s.Fields;
        int o = s.HasSeconds ? 1 : 0;
        string fMin = f[o], fHour = f[o + 1];
        var parts = new List<string>();

        string time;
        if (fMin == "*" && fHour == "*") time = "Every minute";
        else if (fMin.StartsWith("*/") && fHour == "*") time = $"Every {fMin[2..]} minutes";
        else if (fHour.StartsWith("*/") && s.Min.Count == 1) time = $"At minute {s.Min.First()}, every {fHour[2..]} hours";
        else if (fHour == "*") time = $"At minute {ListText(s.Min)} of every hour";
        else if (s.Min.Count * s.Hour.Count <= 8)
            time = "At " + Join(s.Hour.SelectMany(h => s.Min.Select(m => $"{h:00}:{m:00}")).ToList());
        else time = $"At minute {ListText(s.Min)} past hour {ListText(s.Hour)}";

        if (s.HasSeconds)
            time = (f[0] == "*" ? "Every second" : f[0].StartsWith("*/") ? $"Every {f[0][2..]} seconds" : $"At second {ListText(s.Sec)}") + "; " + char.ToLowerInvariant(time[0]) + time[1..];
        parts.Add(time);

        string domText = s.DomStar ? "" : f[o + 2].StartsWith("*/") ? $"every {f[o + 2][2..]} days" : $"day-of-month {ListText(s.Dom)}";
        string dowText = s.DowStar ? "" : DowRange(s.Dow);
        if (domText.Length > 0 && dowText.Length > 0) parts.Add($"on {domText} or on {dowText}");
        else if (domText.Length > 0) parts.Add(domText.StartsWith("every") ? domText : "on " + domText);
        else if (dowText.Length > 0) parts.Add("on " + dowText);

        if (!IsStar(f[o + 3]))
            parts.Add("in " + Join(s.Mon.Select(m => MonthFull[m - 1]).ToList()));

        return string.Join(", ", parts);
    }

    private static string ListText(SortedSet<int> set)
    {
        var runs = Runs(set);
        return Join(runs.Select(r => r.len >= 3 ? $"{r.start} through {r.start + r.len - 1}" : string.Join(", ", Enumerable.Range(r.start, r.len))).ToList());
    }

    private static string DowRange(SortedSet<int> set)
    {
        var runs = Runs(set);
        return Join(runs.Select(r => r.len >= 3
            ? $"{DowFull[r.start]} through {DowFull[r.start + r.len - 1]}"
            : string.Join(" and ", Enumerable.Range(r.start, r.len).Select(i => DowFull[i]))).ToList());
    }

    private static List<(int start, int len)> Runs(SortedSet<int> set)
    {
        var runs = new List<(int, int)>();
        foreach (var v in set)
        {
            if (runs.Count > 0 && runs[^1].Item1 + runs[^1].Item2 == v) runs[^1] = (runs[^1].Item1, runs[^1].Item2 + 1);
            else runs.Add((v, 1));
        }
        return runs;
    }

    private static string Join(List<string> items)
        => items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
