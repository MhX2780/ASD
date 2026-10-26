using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ASD;

public sealed class DiffPage : ToolPage
{
    private readonly TextBox _a, _b;
    private readonly CheckBox _ws, _case, _json, _changesOnly;
    private readonly RichTextBlock _view = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas") };

    private enum Kind { Same, Added, Removed }

    public DiffPage() : base("diff", "Diff Checker", "Compare two texts line by line. Optionally normalize JSON first so key order and whitespace don't matter.")
    {
        _a = Input("a", "Original", 220, "Paste the original text…");
        _b = Input("b", "Changed", 220, "Paste the changed text…");
        _ws = Check("Ignore whitespace");
        _case = Check("Ignore case");
        _json = Check("Format + sort JSON first");
        _changesOnly = Check("Show only changes (±2 lines)");

        Body.Children.Add(TwoCols(_a, _b));
        Body.Children.Add(Row(_ws, _case, _json, _changesOnly));
        Body.Children.Add(Row(
            Btn("Compare", Compare, true),
            Btn("Swap", () => (_a.Text, _b.Text) = (_b.Text, _a.Text)),
            Btn("Clear", () => { _a.Text = ""; _b.Text = ""; _view.Blocks.Clear(); Status.Text = ""; })));
        Body.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8),
            Child = new ScrollViewer
            {
                Height = 380,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _view,
            },
        });
    }

    private string Prepare(string? text)
    {
        text ??= "";
        if (Is(_json))
        {
            try { return JsonTools.Format(JsonTools.SortKeys(JsonTools.Parse(text))); } catch { /* not JSON: compare as text */ }
        }
        return text;
    }

    private static string[] Lines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private string Key(string line)
    {
        if (Is(_ws)) line = Regex.Replace(line.Trim(), @"\s+", " ");
        if (Is(_case)) line = line.ToLowerInvariant();
        return line;
    }

    private void Compare()
    {
        var a = Lines(Prepare(_a.Text));
        var b = Lines(Prepare(_b.Text));
        var ka = a.Select(Key).ToArray();
        var kb = b.Select(Key).ToArray();

        int pre = 0;
        while (pre < a.Length && pre < b.Length && ka[pre] == kb[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && ka[a.Length - 1 - suf] == kb[b.Length - 1 - suf]) suf++;

        int n = a.Length - pre - suf, m = b.Length - pre - suf;
        if ((long)n * m > 16_000_000)
        {
            Status.Text = "These texts differ in too many lines to compare here (limit ≈ 4000 changed lines each).";
            return;
        }

        var result = new List<(Kind, string)>();
        for (int i = 0; i < pre; i++) result.Add((Kind.Same, a[i]));

        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = ka[pre + i] == kb[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (ka[pre + x] == kb[pre + y]) { result.Add((Kind.Same, a[pre + x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { result.Add((Kind.Removed, a[pre + x])); x++; }
            else { result.Add((Kind.Added, b[pre + y])); y++; }
        }
        while (x < n) { result.Add((Kind.Removed, a[pre + x])); x++; }
        while (y < m) { result.Add((Kind.Added, b[pre + y])); y++; }
        for (int i = a.Length - suf; i < a.Length; i++) result.Add((Kind.Same, a[i]));

        int added = result.Count(r => r.Item1 == Kind.Added), removed = result.Count(r => r.Item1 == Kind.Removed);
        Status.Text = added + removed == 0 ? "Identical ✔" : $"+{added} added   −{removed} removed   {result.Count - added - removed} unchanged";
        Render(result);
    }

    private void Render(List<(Kind Kind, string Text)> lines)
    {
        _view.Blocks.Clear();
        var para = new Paragraph();
        var green = new SolidColorBrush(Color.FromArgb(255, 63, 185, 80));
        var red = new SolidColorBrush(Color.FromArgb(255, 248, 81, 73));
        var gray = new SolidColorBrush(Microsoft.UI.Colors.Gray);

        var show = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (!Is(_changesOnly) || lines[i].Kind != Kind.Same) { for (int k = Math.Max(0, i - 2); k <= Math.Min(lines.Count - 1, i + 2); k++) show[k] = true; }
        }

        bool gap = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!show[i] && Is(_changesOnly)) { gap = true; continue; }
            if (gap) { para.Inlines.Add(new Run { Text = "  ⋯", Foreground = gray }); para.Inlines.Add(new LineBreak()); gap = false; }
            var (kind, text) = lines[i];
            var run = kind switch
            {
                Kind.Added => new Run { Text = "+ " + text, Foreground = green },
                Kind.Removed => new Run { Text = "- " + text, Foreground = red },
                _ => new Run { Text = "  " + text, Foreground = gray },
            };
            para.Inlines.Add(run);
            para.Inlines.Add(new LineBreak());
        }
        _view.Blocks.Add(para);
    }
}
