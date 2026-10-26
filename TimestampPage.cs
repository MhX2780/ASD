using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

public sealed class TimestampPage : ToolPage
{
    private readonly TextBox _in, _out;
    private readonly ComboBox _zone;
    private readonly TextBlock _now = new() { FontFamily = new FontFamily("Consolas"), Opacity = 0.85 };
    private DispatcherTimer? _timer;

    public TimestampPage() : base("time", "Timestamp Converter", "Unix time ↔ human dates. Detects seconds, milliseconds, microseconds and nanoseconds automatically.")
    {
        _in = Input("in", "Unix timestamp or date/time", placeholder: "1790000000   |   2026-09-29T18:36:00Z   |   29 Sep 2026 18:36", multiline: false);
        _zone = Combo(new[] { "Local", "UTC" }.Concat(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id)).ToArray());
        _zone.MinWidth = 260;
        _out = Output("Result", 300);

        _in.TextChanged += (_, _) => Convert();
        _zone.SelectionChanged += (_, _) => Convert();

        Body.Children.Add(_now);
        Body.Children.Add(Row(
            Btn("Now", () => _in.Text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), true),
            Btn("Now (ms)", () => _in.Text = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
            Label("Show in:"), _zone));
        Body.Children.Add(_in);
        Body.Children.Add(_out);
        Body.Children.Add(CopyBtn("Copy result", () => _out.Text));

        Loaded += (_, _) =>
        {
            UpdateNow();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => UpdateNow();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    protected override void OnClipboardInput(string text) => _in.Text = text.Trim();

    private void UpdateNow()
    {
        var n = DateTimeOffset.UtcNow;
        _now.Text = $"Now:  {n.ToUnixTimeSeconds()} s   |   {n.ToUnixTimeMilliseconds()} ms   |   {n.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC";
    }

    private TimeZoneInfo SelectedZone()
    {
        var id = Sel(_zone);
        try
        {
            return id switch { "Local" or "" => TimeZoneInfo.Local, "UTC" => TimeZoneInfo.Utc, _ => TimeZoneInfo.FindSystemTimeZoneById(id) };
        }
        catch { return TimeZoneInfo.Local; }
    }

    private void Convert()
    {
        var s = (_in.Text ?? "").Trim();
        if (s.Length == 0) { _out.Text = ""; Status.Text = ""; return; }

        DateTimeOffset dt;
        string kind;
        try
        {
            if (Regex.IsMatch(s, @"^-?\d+(\.\d+)?$"))
            {
                var digits = s.Split('.')[0].TrimStart('-').Length;
                var num = decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
                decimal seconds;
                if (digits <= 11) { seconds = num; kind = "seconds"; }
                else if (digits <= 14) { seconds = num / 1_000m; kind = "milliseconds"; }
                else if (digits <= 17) { seconds = num / 1_000_000m; kind = "microseconds"; }
                else { seconds = num / 1_000_000_000m; kind = "nanoseconds"; }
                dt = DateTimeOffset.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));
            }
            else if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
                kind = "date / time text (no offset = local time)";
            else
            {
                _out.Text = "";
                Status.Text = "Could not understand that value.";
                return;
            }
        }
        catch (Exception ex)
        {
            _out.Text = "";
            Status.Text = "Error: " + ex.Message;
            return;
        }

        Status.Text = "";
        var tz = SelectedZone();
        var inZone = TimeZoneInfo.ConvertTime(dt, tz);
        var utc = dt.UtcDateTime;
        var local = TimeZoneInfo.ConvertTime(dt, TimeZoneInfo.Local);
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        sb.AppendLine($"Detected     : {kind}");
        sb.AppendLine($"Relative     : {TimeTools.Relative(dt, DateTimeOffset.UtcNow)}");
        sb.AppendLine();
        sb.AppendLine($"UTC          : {utc.ToString("ddd, dd MMM yyyy HH:mm:ss", inv)}");
        sb.AppendLine($"Local        : {local.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", inv)}");
        sb.AppendLine($"{tz.Id,-13}: {inZone.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", inv)}");
        sb.AppendLine();
        sb.AppendLine($"ISO 8601     : {utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", inv)}");
        sb.AppendLine($"RFC 2822     : {utc.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", inv)}");
        sb.AppendLine($"Unix seconds : {dt.ToUnixTimeSeconds()}");
        sb.AppendLine($"Unix millis  : {dt.ToUnixTimeMilliseconds()}");
        sb.AppendLine($"Day of year  : {utc.DayOfYear}     ISO week: {ISOWeek.GetWeekOfYear(utc)}");
        _out.Text = sb.ToString().TrimEnd();
    }
}
