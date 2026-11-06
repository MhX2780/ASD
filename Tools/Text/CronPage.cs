using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class CronPage : ToolPage
{
    private static readonly (string Label, string Expr)[] Examples =
    {
        ("Every minute", "* * * * *"),
        ("Every 5 minutes", "*/5 * * * *"),
        ("Every hour, on the hour", "0 * * * *"),
        ("Every day at 09:00", "0 9 * * *"),
        ("Weekdays at 08:30", "30 8 * * 1-5"),
        ("Every Monday at 03:00", "0 3 * * MON"),
        ("1st of every month at midnight", "0 0 1 * *"),
        ("Every 15 seconds (6 fields)", "*/15 * * * * *"),
    };

    private readonly TextBox _expr, _runs;
    private readonly TextBlock _explain = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ComboBox _examples;

    public CronPage() : base("cron", "Cron Explainer", "Paste a cron expression to get a plain-English explanation and its next run times (5 fields, or 6 with seconds; @daily-style macros work too).")
    {
        _expr = Input("expr", "Cron expression", placeholder: "*/15 9-17 * * MON-FRI", multiline: false);
        _examples = Combo(new[] { "Examples…" }.Concat(Examples.Select(e => $"{e.Expr}   —   {e.Label}")).ToArray());
        _runs = Output("Next 10 runs (your local time)", 260);

        _expr.TextChanged += (_, _) => Update();
        _examples.SelectionChanged += (_, _) =>
        {
            if (_examples.SelectedIndex > 0) _expr.Text = Examples[_examples.SelectedIndex - 1].Expr;
        };

        Body.Children.Add(_expr);
        Body.Children.Add(_examples);
        Body.Children.Add(_explain);
        Body.Children.Add(_runs);
    }

    protected override void OnClipboardInput(string text) => _expr.Text = text.Trim();

    private void Update()
    {
        var text = _expr.Text ?? "";
        if (text.Trim().Length == 0) { _explain.Text = ""; _runs.Text = ""; Status.Text = ""; return; }
        try
        {
            var spec = CronTools.Parse(text);
            _explain.Text = CronTools.Describe(spec);
            var runs = CronTools.NextRuns(spec, DateTime.Now, 10);
            var now = DateTimeOffset.Now;
            _runs.Text = runs.Count == 0
                ? "No run found in the next 8 years."
                : string.Join(Environment.NewLine, runs.Select(r => $"{r:ddd yyyy-MM-dd HH:mm:ss}    ({TimeTools.Relative(new DateTimeOffset(r), now)})"));
            Status.Text = "";
        }
        catch (Exception ex)
        {
            _explain.Text = "";
            _runs.Text = "";
            Status.Text = "⚠ " + ex.Message;
        }
    }
}
