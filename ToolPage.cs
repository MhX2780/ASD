using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

/// <summary>
/// Base class for code-built tool pages: title, scrolling body, status line, helpers,
/// and automatic saving/restoring of the input boxes (never secrets: pass persist:false).
/// Pages are created directly by MainWindow (no Frame), which calls Activate/Deactivate.
/// </summary>
public abstract class ToolPage : Page
{
    private readonly string _stateKey;
    private readonly List<(string Key, TextBox Box)> _persisted = new();

    protected readonly StackPanel Body = new() { Spacing = 12 };
    protected readonly TextBlock Status = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    protected ToolPage(string stateKey, string title, string subtitle)
    {
        _stateKey = stateKey;
        var header = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.Bold },
                new TextBlock { Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Padding = new Thickness(32), Spacing = 14, Children = { header, Body, Status } },
        };
        Loaded += (_, _) => CursorHelper.ApplyHandCursorToButtons(this);
    }

    // ── lifecycle (called by MainWindow) ──
    internal void Activate(string? parameter)
    {
        foreach (var (key, box) in _persisted)
        {
            var saved = SettingsStore.Get($"state.{_stateKey}.{key}", "");
            if (saved.Length > 0) box.Text = saved;
        }
        if (!string.IsNullOrEmpty(parameter)) OnClipboardInput(parameter);
    }

    internal void Deactivate()
    {
        SettingsStore.SetMany(_persisted.Select(p =>
            new KeyValuePair<string, string>($"state.{_stateKey}.{p.Key}", Trunc(p.Box.Text))));
    }

    private static string Trunc(string? s) => s == null ? "" : s.Length > 100_000 ? s[..100_000] : s;

    /// <summary>Called when the user opens this tool from the clipboard banner.</summary>
    protected virtual void OnClipboardInput(string text) { }

    // ── builders ──
    protected TextBox Input(string key, string header, double height = 140, string? placeholder = null,
                            bool persist = true, bool multiline = true)
    {
        var tb = MakeBox(header, height, placeholder, multiline);
        if (persist) _persisted.Add((key, tb));
        return tb;
    }

    protected static TextBox Output(string header, double height = 140)
    {
        var tb = MakeBox(header, height, null, true);
        tb.IsReadOnly = true;
        return tb;
    }

    private static TextBox MakeBox(string header, double height, string? placeholder, bool multiline)
    {
        var tb = new TextBox
        {
            Header = string.IsNullOrEmpty(header) ? null : header,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            PlaceholderText = placeholder ?? "",
            IsSpellCheckEnabled = false,
            FontFamily = new FontFamily("Consolas"),
        };
        if (multiline) tb.Height = height;
        ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
        return tb;
    }

    protected Button Btn(string text, Action onClick, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var st) && st is Style style)
            b.Style = style;
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Fail(ex); }
        };
        return b;
    }

    protected Button CopyBtn(string label, Func<string> getText)
        => Btn(label, () => Copy(getText()));

    protected static TextBlock Label(string text)
        => new() { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    protected static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { MinWidth = 170 };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = 0;
        return c;
    }

    protected static string Sel(ComboBox c) => c.SelectedItem as string ?? "";

    protected static CheckBox Check(string text, bool on = false)
        => new() { Content = text, IsChecked = on };

    protected static bool Is(CheckBox c) => c.IsChecked == true;

    protected static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var it in items)
        {
            if (it is FrameworkElement fe && it is not TextBox) fe.VerticalAlignment = VerticalAlignment.Center;
            p.Children.Add(it);
        }
        return p;
    }

    protected static Grid TwoCols(UIElement left, UIElement right)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    /// <summary>A "label | read-only box | Copy" row appended to <paramref name="host"/>.</summary>
    protected TextBox ResultRow(Panel host, string label, double labelWidth = 110)
    {
        var box = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = Label(label);
        var copy = CopyBtn("Copy", () => box.Text);
        Grid.SetColumn(box, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(lbl);
        grid.Children.Add(box);
        grid.Children.Add(copy);
        host.Children.Add(grid);
        return box;
    }

    protected void Copy(string? text)
    {
        var package = new DataPackage();
        package.SetText(text ?? string.Empty);
        Clipboard.SetContent(package);
        Status.Text = "Copied!";
    }

    protected void Fail(Exception ex) => Status.Text = $"Error: {ex.Message}";
}
