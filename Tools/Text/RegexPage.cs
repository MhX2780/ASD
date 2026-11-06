using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ASD;

public sealed class RegexPage : ToolPage
{
    private sealed record MatchRec(int Index, int Length, string Value, List<(string Name, string Value)> Groups);

    private readonly TextBox _pattern, _text, _replace, _matches, _replaced, _explain;
    private readonly CheckBox _ic, _ml, _sl, _iw, _ec;
    private readonly RichTextBlock _view = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
    private int _ver;

    public RegexPage() : base("regex", "Regex Tester", "Live matching with highlights, groups, replace and a plain-English explanation (.NET regex syntax).")
    {
        _pattern = Input("pattern", "Pattern", placeholder: @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})", multiline: false);
        _ic = Check("Ignore case"); _ml = Check("Multiline (^ $ per line)"); _sl = Check("Singleline (. matches \\n)");
        _iw = Check("Ignore pattern whitespace"); _ec = Check("Explicit capture");
        _text = Input("text", "Test text", 170, "Paste the text to search…");
        _replace = Input("replace", "Replacement (optional) — use $1 or ${name}", placeholder: "$3/$2/$1", multiline: false);
        _matches = Output("Matches and groups", 200);
        _replaced = Output("Replace result", 120);
        _explain = Output("Explanation", 200);

        foreach (var cb in new[] { _ic, _ml, _sl, _iw, _ec }) { cb.Checked += (_, _) => Refresh(); cb.Unchecked += (_, _) => Refresh(); }
        _pattern.TextChanged += (_, _) => Refresh();
        _text.TextChanged += (_, _) => Refresh();
        _replace.TextChanged += (_, _) => Refresh();

        Body.Children.Add(_pattern);
        Body.Children.Add(Row(_ic, _ml, _sl, _iw, _ec));
        Body.Children.Add(_text);
        Body.Children.Add(Label("Highlighted matches"));
        Body.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Colors.Gray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new ScrollViewer { Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _view },
        });
        Body.Children.Add(TwoCols(_matches, _explain));
        Body.Children.Add(_replace);
        Body.Children.Add(_replaced);
    }

    private RegexOptions Options()
    {
        var o = RegexOptions.None;
        if (Is(_ic)) o |= RegexOptions.IgnoreCase;
        if (Is(_ml)) o |= RegexOptions.Multiline;
        if (Is(_sl)) o |= RegexOptions.Singleline;
        if (Is(_iw)) o |= RegexOptions.IgnorePatternWhitespace;
        if (Is(_ec)) o |= RegexOptions.ExplicitCapture;
        return o;
    }

    private async void Refresh()
    {
        var v = ++_ver;
        var pattern = _pattern.Text ?? "";
        var text = _text.Text ?? "";
        var opts = Options();
        var rep = string.IsNullOrEmpty(_replace.Text) ? null : _replace.Text;

        if (pattern.Length == 0)
        {
            _view.Blocks.Clear(); _matches.Text = _replaced.Text = _explain.Text = ""; Status.Text = "";
            return;
        }
        try { _explain.Text = RegexExplainer.Explain(pattern); } catch { _explain.Text = ""; }

        var (matches, replaced, error) = await Task.Run(() => Evaluate(pattern, text, opts, rep));
        if (v != _ver) return;

        if (error != null)
        {
            Status.Text = "⚠ " + error;
            _view.Blocks.Clear(); _matches.Text = _replaced.Text = "";
            return;
        }
        Status.Text = $"{matches!.Count} match{(matches.Count == 1 ? "" : "es")}";
        _replaced.Text = replaced ?? "";

        var sb = new StringBuilder();
        int n = 1;
        foreach (var m in matches)
        {
            sb.AppendLine($"#{n++}  [{m.Index}–{m.Index + m.Length})  \"{m.Value}\"");
            foreach (var (name, val) in m.Groups) sb.AppendLine($"      {name} = {val}");
        }
        _matches.Text = sb.ToString().TrimEnd();
        Highlight(text, matches);
    }

    private static (List<MatchRec>? matches, string? replaced, string? error) Evaluate(string pattern, string text, RegexOptions opts, string? replacement)
    {
        try
        {
            var rx = new Regex(pattern, opts, TimeSpan.FromSeconds(2));
            var list = new List<MatchRec>();
            foreach (Match m in rx.Matches(text))
            {
                var gs = new List<(string, string)>();
                for (int g = 1; g < m.Groups.Count; g++)
                {
                    var grp = m.Groups[g];
                    gs.Add((rx.GroupNameFromNumber(g), grp.Success ? grp.Value : "(no match)"));
                }
                list.Add(new MatchRec(m.Index, m.Length, m.Value, gs));
                if (list.Count >= 2000) break;
            }
            return (list, replacement != null ? rx.Replace(text, replacement) : null, null);
        }
        catch (RegexMatchTimeoutException) { return (null, null, "Timed out — the pattern may cause catastrophic backtracking."); }
        catch (ArgumentException ex) { return (null, null, ex.Message); }
    }

    private void Highlight(string text, List<MatchRec> matches)
    {
        _view.Blocks.Clear();
        var para = new Paragraph();
        var hi = new SolidColorBrush(Colors.Orange);
        int pos = 0;
        foreach (var m in matches.Where(x => x.Length > 0))
        {
            if (m.Index < pos) continue;
            if (m.Index > pos) para.Inlines.Add(new Run { Text = text.Substring(pos, m.Index - pos) });
            para.Inlines.Add(new Run { Text = text.Substring(m.Index, m.Length), Foreground = hi, FontWeight = FontWeights.Bold });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) para.Inlines.Add(new Run { Text = text[pos..] });
        _view.Blocks.Add(para);
    }
}
