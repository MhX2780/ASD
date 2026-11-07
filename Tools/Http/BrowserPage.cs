using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace ASD;

/// <summary>
/// Web Browser (Toolbox): the browser itself is a window of its own (BrowserWindow). This page is the Inspect side:
/// a bar of tabs like the Extensions page (Network, Cookies, Source) for the page that is open in the browser window,
/// and a DevTools button for Edge's full developer tools. Everything slow (reading the source, the cookies, filtering
/// thousands of requests) happens in the background and shows a progress ring.
/// </summary>
public sealed class BrowserPage : ToolPage
{
    /// <summary>A new page each time (the browser window keeps the tabs, so this page has nothing to keep).</summary>
    public static BrowserPage Instance => new();

    private BrowserWindow? _window;

    // the line under the title: what is inspected, and the progress ring
    private readonly TextBlock _target = new() { Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _ring = new() { Width = 22, Height = 22, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private int _busy;

    private readonly SelectorBar _bar = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _stage = new();
    private readonly UIElement _netPanel, _cookiePanel, _sourcePanel;

    private readonly ObservableCollection<NetRow> _net = new();
    private readonly TextBox _filter = new() { PlaceholderText = "Filter (url, method, status, type)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _netList = new() { Height = 340, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 220, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly ListView _cookieList = new() { Height = 420, SelectionMode = ListViewSelectionMode.None };
    private List<CoreWebView2Cookie> _cookies = new();
    private readonly TextBox _source = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 520, FontFamily = new FontFamily("Cascadia Mono, Consolas") };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _dirty;
    private long _lastId;
    private const int ShownRows = 1500;

    private BrowserPage() : base("browser", "Web Browser", "The browser opens in its own window. This page inspects the page that is open there.")
    {
        SetHeader("Web Browser", "The browser is in its own window. Inspect its page here: network, cookies, source.",
            ActionBtn("", "Open browser", () => BrowserWindow.ShowWindow()),
            ActionBtn("", "DevTools", () => _window?.Core?.OpenDevToolsWindow()));

        _netPanel = BuildNetwork();
        _cookiePanel = BuildCookies();
        _sourcePanel = BuildSource();
        _stage.Children.Add(_netPanel);
        _stage.Children.Add(_cookiePanel);
        _stage.Children.Add(_sourcePanel);

        _bar.Items.Add(new SelectorBarItem { Text = "Network", Tag = "net" });
        _bar.Items.Add(new SelectorBarItem { Text = "Cookies", Tag = "cookies" });
        _bar.Items.Add(new SelectorBarItem { Text = "Source", Tag = "source" });
        _bar.SelectedItem = _bar.Items[0];
        _bar.SelectionChanged += (_, _) => ShowPanel();

        var targetRow = new Grid { ColumnSpacing = 10 };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_ring, 1);
        targetRow.Children.Add(_target);
        targetRow.Children.Add(_ring);

        Body.Children.Add(targetRow);
        Body.Children.Add(_bar);
        Body.Children.Add(_stage);
        ShowPanel();

        _timer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            AppendNetwork();
        };
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>A web address copied to the clipboard and opened here from the clipboard banner: it opens in the browser window.</summary>
    protected override void OnClipboardInput(string text) => BrowserWindow.ShowWindow(text);

    // ───────── the link to the browser window ─────────

    private void Attach()
    {
        _window = BrowserWindow.ShowWindow();
        _window.NetworkChanged += OnNetworkChanged;
        _window.CurrentChanged += OnCurrentChanged;
        _window.WindowClosed += OnWindowClosed;
        _timer.Start();
        OnCurrentChanged();
        CursorHelper.ApplyHandCursorToButtons(this);
    }

    private void Detach()
    {
        _timer.Stop();
        if (_window == null) return;
        _window.NetworkChanged -= OnNetworkChanged;
        _window.CurrentChanged -= OnCurrentChanged;
        _window.WindowClosed -= OnWindowClosed;
        _window = null;
    }

    private void OnNetworkChanged() => _dirty = true;

    private void OnCurrentChanged()
    {
        UpdateTarget();
        RebuildNetwork();
        var tag = CurrentTag();
        if (tag == "cookies") _ = LoadCookiesAsync();
        else if (tag == "source") _ = LoadSourceAsync();
    }

    private void OnWindowClosed()
    {
        _window = null;
        _net.Clear();
        _cookieList.ItemsSource = null;
        _source.Text = "";
        _details.Text = "";
        _target.Text = "The browser window is closed. Press Open browser to start it again.";
    }

    private void UpdateTarget()
    {
        var w = _window;
        _target.Text = w == null ? "The browser window is closed. Press Open browser to start it again."
            : w.Core == null ? "The page is loading…"
            : $"Inspecting:  {(w.PageTitle.Length > 0 ? w.PageTitle + "  ·  " : "")}{w.PageUrl}";
    }

    // ───────── the progress ring ─────────

    private void BeginBusy()
    {
        _busy++;
        _ring.IsActive = true;
        _ring.Visibility = Visibility.Visible;
    }

    private void EndBusy()
    {
        _busy = Math.Max(0, _busy - 1);
        if (_busy > 0) return;
        _ring.IsActive = false;
        _ring.Visibility = Visibility.Collapsed;
    }

    // ───────── the tabs ─────────

    private string CurrentTag() => (_bar.SelectedItem as SelectorBarItem)?.Tag as string ?? "net";

    private void ShowPanel()
    {
        var tag = CurrentTag();
        _netPanel.Visibility = tag == "net" ? Visibility.Visible : Visibility.Collapsed;
        _cookiePanel.Visibility = tag == "cookies" ? Visibility.Visible : Visibility.Collapsed;
        _sourcePanel.Visibility = tag == "source" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "cookies") _ = LoadCookiesAsync();
        else if (tag == "source") _ = LoadSourceAsync();
    }

    // ── Network ──

    private UIElement BuildNetwork()
    {
        _netList.ItemsSource = _net;
        _netList.SelectionChanged += (_, _) => ShowDetails();
        _filter.TextChanged += (_, _) => RebuildNetwork();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Btn("Copy as cURL", () => { if (Selected() is { } r) Copy(CurlOf(r)); }));
        actions.Children.Add(Btn("cURL → Code", () => { if (Selected() is { } r) (App.MainWindow as MainWindow)?.OpenTool("curl", CurlOf(r)); }));
        actions.Children.Add(Btn("HTTP Track", () => { if (Selected() is { } r) (App.MainWindow as MainWindow)?.OpenTool("httptrack", r.Url); }));
        actions.Children.Add(Btn("Clear", () => { _window?.ClearLog(); _net.Clear(); _details.Text = ""; }));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(_filter);
        panel.Children.Add(_netList);
        panel.Children.Add(actions);
        panel.Children.Add(_details);
        return panel;
    }

    private NetRow? Selected() => _netList.SelectedItem as NetRow;

    /// <summary>The whole list again (another tab, a new filter): filtered in the background, with the ring on.</summary>
    private async void RebuildNetwork()
    {
        try
        {
            var w = _window;
            if (w == null) { _net.Clear(); return; }
            var snapshot = w.Log.ToArray();
            var f = _filter.Text.Trim();
            BeginBusy();
            try
            {
                var rows = await Task.Run(() => snapshot.Where(r => r.Matches(f)).TakeLast(ShownRows).ToList());
                _net.Clear();
                foreach (var r in rows) _net.Add(r);
                _lastId = snapshot.Length > 0 ? snapshot[^1].Id : 0;
                _details.Text = "";
            }
            finally { EndBusy(); }
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
    }

    /// <summary>Only the requests that arrived since the last time (a few times a second, not once per request).</summary>
    private void AppendNetwork()
    {
        var w = _window;
        if (w == null) return;
        var log = w.Log;
        var fresh = new List<NetRow>();
        for (int i = log.Count - 1; i >= 0 && log[i].Id > _lastId; i--) fresh.Add(log[i]);
        if (fresh.Count == 0) return;
        fresh.Reverse();
        _lastId = fresh[^1].Id;

        var f = _filter.Text.Trim();
        foreach (var r in fresh) if (r.Matches(f)) _net.Add(r);
        while (_net.Count > ShownRows) _net.RemoveAt(0);
    }

    private void ShowDetails()
    {
        if (Selected() is not { } r) { _details.Text = ""; return; }
        _details.Text = $"{r.Method} {r.Url}\n{r.Status} {r.Reason}\n\n── Request headers ──\n{r.RequestHeaders}\n\n── Response headers ──\n{r.ResponseHeaders}";
    }

    /// <summary>The request as a cURL command (the body of a POST is not available from the browser).</summary>
    private static string CurlOf(NetRow r)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var sb = new StringBuilder("curl");
        if (!r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)) sb.Append(" -X ").Append(r.Method);
        foreach (var line in r.RequestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(" -H ").Append(Q(line.TrimEnd('\r')));
        sb.Append(' ').Append(Q(r.Url));
        return sb.ToString();
    }

    // ── Cookies ──

    private UIElement BuildCookies()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Btn("Refresh", () => _ = LoadCookiesAsync()));
        actions.Children.Add(Btn("Copy as Cookie header", () => Copy(string.Join("; ", _cookies.Select(c => $"{c.Name}={c.Value}")))));
        actions.Children.Add(Btn("Delete all", () =>
        {
            var core = _window?.Core;
            if (core == null) return;
            foreach (var cookie in _cookies) core.CookieManager.DeleteCookie(cookie);
            _ = LoadCookiesAsync();
        }));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Cookies of the page that is open in the browser window.", Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(actions);
        panel.Children.Add(_cookieList);
        return panel;
    }

    private async Task LoadCookiesAsync()
    {
        var core = _window?.Core;
        if (core == null) { _cookieList.ItemsSource = null; return; }
        BeginBusy();
        try
        {
            var found = await core.CookieManager.GetCookiesAsync(core.Source);
            _cookies = found.ToList();
            _cookieList.ItemsSource = _cookies.Select(c =>
                $"{c.Name} = {Cut(c.Value, 60)}\n{c.Domain}{c.Path}  ·  {(c.IsSession ? "session" : c.Expires.ToString("yyyy-MM-dd HH:mm"))}"
                + (c.IsSecure ? "  ·  Secure" : "") + (c.IsHttpOnly ? "  ·  HttpOnly" : "")).ToList();
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
        finally { EndBusy(); }
    }

    // ── Source ──

    private UIElement BuildSource()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Btn("Reload source", () => _ = LoadSourceAsync()));
        actions.Children.Add(Btn("Copy", () => Copy(_source.Text)));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "The page as it is now (after scripts ran).", Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(actions);
        panel.Children.Add(_source);
        return panel;
    }

    private async Task LoadSourceAsync()
    {
        var core = _window?.Core;
        if (core == null) { _source.Text = ""; return; }
        BeginBusy();
        try
        {
            var json = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = await Task.Run(() => JsonSerializer.Deserialize<string>(json) ?? "");   // a big page: decoded in the background
            _source.Text = html.Length > 400_000 ? html[..400_000] + "\n… (cut: the page is longer)" : html;
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
        finally { EndBusy(); }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
