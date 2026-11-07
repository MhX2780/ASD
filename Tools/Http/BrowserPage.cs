using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace ASD;

/// <summary>
/// Web Browser: tabs, an address / search bar and an "Inspect" panel (Network, Cookies, Source) on top of Microsoft Edge's
/// WebView2. A request in the Network list can be copied as cURL, sent to "cURL → Code" or followed in "HTTP Track".
/// "DevTools" opens Edge's own developer tools for the page.
/// The page is created once and kept, so the tabs stay open while another tool is shown.
/// </summary>
public sealed class BrowserPage : ToolPage
{
    private static BrowserPage? _instance;
    public static BrowserPage Instance => _instance ??= new BrowserPage();

    private const string HomeUrl = "https://duckduckgo.com/";
    private const int MaxRows = 3000;

    // ───────── data ─────────

    /// <summary>One request the page made (from WebView2's response event).</summary>
    private sealed class NetRow
    {
        public string Method = "", Url = "", Reason = "", ContentType = "", RequestHeaders = "", ResponseHeaders = "";
        public int Status;
        public long Length = -1;

        public override string ToString()
        {
            var kind = ContentType.Split(';')[0].Split('/').LastOrDefault() ?? "";
            var size = Length < 0 ? "" : Length < 1024 ? $"{Length} B" : Length < 1048576 ? $"{Length / 1024.0:0.#} KB" : $"{Length / 1048576.0:0.#} MB";
            return $"{Status}   {Method}   {Shorten(Url, 80)}   {kind}   {size}".TrimEnd();
        }
    }

    private sealed class BrowserTab
    {
        public readonly WebView2 View = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Visibility = Visibility.Collapsed };
        public TabViewItem Item = new();
        public readonly List<NetRow> Log = new();
        public bool Loading;
    }

    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _current;
    private static CoreWebView2Environment? _environment;

    // ───────── controls ─────────

    private readonly TabView _tabView = new()
    {
        IsAddTabButtonVisible = true,
        TabWidthMode = TabViewWidthMode.SizeToContent,
        CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly Grid _views = new();                     // every tab's WebView2; only the selected one is visible
    private readonly TextBox _address = new() { PlaceholderText = "Search or enter a web address", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _back, _forward, _reload;
    private readonly ToggleButton _inspect;
    private readonly Grid _area = new() { RowSpacing = 6 };
    private readonly Border _panel;

    private readonly ObservableCollection<NetRow> _net = new();
    private readonly TextBox _filter = new() { PlaceholderText = "Filter (url, method, status, type)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _netList = new() { Height = 230, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 170, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly ListView _cookieList = new() { Height = 330, SelectionMode = ListViewSelectionMode.None };
    private List<CoreWebView2Cookie> _cookies = new();
    private readonly TextBox _source = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 390, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly Pivot _pivot = new();

    private BrowserPage() : base("browser", "Web Browser",
        "Browse with tabs, and look inside the page: network requests, cookies and source. DevTools opens Edge's full developer tools.")
    {
        _back = IconButton("", "Back", () => _current?.View.CoreWebView2?.GoBack());
        _forward = IconButton("", "Forward", () => _current?.View.CoreWebView2?.GoForward());
        _reload = IconButton("", "Reload", ReloadOrStop);
        var devTools = IconButton("", "DevTools: Edge's full developer tools for this page", () => _current?.View.CoreWebView2?.OpenDevToolsWindow());
        _inspect = new ToggleButton { Content = new FontIcon { Glyph = "", FontSize = 15 }, Padding = new Thickness(10, 7, 10, 7) };
        ToolTipService.SetToolTip(_inspect, "Inspect: network requests, cookies, source");
        _inspect.Click += (_, _) => TogglePanel();

        _address.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            Go(_address.Text);
            e.Handled = true;
        };
        _address.GotFocus += (_, _) => _address.SelectAll();

        // toolbar
        var toolbar = new Grid { ColumnSpacing = 6 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Place(toolbar, _back, 0); Place(toolbar, _forward, 1); Place(toolbar, _reload, 2);
        Place(toolbar, _address, 3); Place(toolbar, _inspect, 4); Place(toolbar, devTools, 5);

        // tabs: only the strip is used; the pages live in _views
        _tabView.AddTabButtonClick += (_, _) => NewTab(HomeUrl);
        _tabView.TabCloseRequested += (_, a) => { if (a.Tab.Tag is BrowserTab t) CloseTab(t); };
        _tabView.SelectionChanged += (_, _) => SwitchTab(_tabView.SelectedItem is TabViewItem { Tag: BrowserTab t } ? t : null);

        _panel = BuildPanel();
        _panel.Visibility = Visibility.Collapsed;

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_views, 0);
        Grid.SetColumn(_panel, 1);
        content.Children.Add(_views);
        content.Children.Add(_panel);

        _area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0); Grid.SetRow(_tabView, 1); Grid.SetRow(content, 2);
        _area.Children.Add(toolbar);
        _area.Children.Add(_tabView);
        _area.Children.Add(content);
        Body.Children.Add(_area);

        // a browser needs a fixed height (the page itself scrolls): the window's height minus the title area
        SizeChanged += (_, e) => _area.Height = Math.Max(420, e.NewSize.Height - 190);
        Loaded += (_, _) => { if (_tabs.Count == 0) NewTab(HomeUrl); };
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 15 }, Padding = new Thickness(10, 7, 10, 7) };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => { try { click(); } catch { /* the page may not be ready yet */ } };
        return button;
    }

    // ───────── tabs ─────────

    private void NewTab(string url)
    {
        var tab = new BrowserTab();
        tab.Item = new TabViewItem { Header = "New tab", Tag = tab, IconSource = new SymbolIconSource { Symbol = Symbol.Globe } };
        _tabs.Add(tab);
        _views.Children.Add(tab.View);
        _tabView.TabItems.Add(tab.Item);
        _tabView.SelectedItem = tab.Item;
        _ = InitTabAsync(tab, url);
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _tabView.TabItems.Remove(tab.Item);
        _views.Children.Remove(tab.View);
        try { tab.View.Close(); } catch { }

        if (_tabs.Count == 0) { _current = null; NewTab(HomeUrl); return; }
        if (_current == tab || _current == null)
            _tabView.SelectedItem = _tabs[Math.Clamp(index, 0, _tabs.Count - 1)].Item;
    }

    private void SwitchTab(BrowserTab? tab)
    {
        _current = tab;
        foreach (var t in _tabs) t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        if (tab == null) return;
        UpdateToolbar();
        RefreshNetwork();
        if (_panel.Visibility == Visibility.Visible) _ = RefreshPivotAsync();
    }

    private void UpdateToolbar()
    {
        var core = _current?.View.CoreWebView2;
        _back.IsEnabled = core?.CanGoBack == true;
        _forward.IsEnabled = core?.CanGoForward == true;
        _address.Text = core?.Source is { Length: > 0 } s && s != "about:blank" ? s : "";
        ((FontIcon)_reload.Content).Glyph = _current?.Loading == true ? "" : "";
    }

    private async Task InitTabAsync(BrowserTab tab, string url)
    {
        try
        {
            _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, EnsureProfileFolder(), new CoreWebView2EnvironmentOptions());
            await tab.View.EnsureCoreWebView2Async(_environment);
            var core = tab.View.CoreWebView2;

            core.DocumentTitleChanged += (_, _) => tab.Item.Header = Shorten(string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New tab" : core.DocumentTitle, 26);
            core.SourceChanged += (_, _) => { if (tab == _current) UpdateToolbar(); };
            core.HistoryChanged += (_, _) => { if (tab == _current) UpdateToolbar(); };
            core.NavigationStarting += (_, _) => { tab.Loading = true; if (tab == _current) UpdateToolbar(); };
            core.NavigationCompleted += (_, _) =>
            {
                tab.Loading = false;
                if (tab != _current) return;
                UpdateToolbar();
                if (_panel.Visibility == Visibility.Visible && _pivot.SelectedIndex > 0) _ = RefreshPivotAsync();
            };
            core.NewWindowRequested += (_, a) => { a.Handled = true; NewTab(a.Uri); };   // links that open a new window become a new tab
            core.WebResourceResponseReceived += (_, a) =>
            {
                try { AddRow(tab, FromEvent(a)); } catch { /* one odd request must not stop the log */ }
            };

            core.Navigate(ToUrl(url));
            UpdateToolbar();
        }
        catch (Exception ex)
        {
            Status.Text = ex.GetType().Name.Contains("NotFound", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase)
                ? "Web Browser needs the Microsoft Edge WebView2 Runtime, which was not found. Install it from https://developer.microsoft.com/microsoft-edge/webview2 and open this tool again."
                : "Error: the browser could not start. " + ex.Message;
        }
    }

    /// <summary>The browser's own profile (cookies, logins, cache), next to ASD's other data.</summary>
    private static string EnsureProfileFolder()
    {
        var folder = AppPaths.File("browser");
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ───────── address bar ─────────

    private void Go(string text)
    {
        var core = _current?.View.CoreWebView2;
        if (core == null) return;
        core.Navigate(ToUrl(text));
    }

    private void ReloadOrStop()
    {
        var core = _current?.View.CoreWebView2;
        if (core == null) return;
        if (_current!.Loading) core.Stop(); else core.Reload();
    }

    /// <summary>A web address as it is; "example.com" gets https://; anything else is a search.</summary>
    private static string ToUrl(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "about:blank";
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" or "about" or "edge") return text;
        if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return "http://" + text;
        if (!text.Contains(' ') && text.Contains('.')) return "https://" + text;
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
    }

    /// <summary>A web address copied to the clipboard and opened here from the clipboard banner.</summary>
    protected override void OnClipboardInput(string text) => Go(text);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ───────── Inspect panel ─────────

    private Border BuildPanel()
    {
        _pivot.Items.Add(new PivotItem { Header = "Network", Content = BuildNetwork() });
        _pivot.Items.Add(new PivotItem { Header = "Cookies", Content = BuildCookies() });
        _pivot.Items.Add(new PivotItem { Header = "Source", Content = BuildSource() });
        _pivot.SelectionChanged += (_, _) => _ = RefreshPivotAsync();
        return new Border
        {
            Width = 430,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
            Child = _pivot,
        };
    }

    private void TogglePanel()
    {
        _panel.Visibility = _inspect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_panel.Visibility == Visibility.Visible) { RefreshNetwork(); _ = RefreshPivotAsync(); }
    }

    private async Task RefreshPivotAsync()
    {
        if (_current?.View.CoreWebView2 == null) return;
        if (_pivot.SelectedIndex == 1) await LoadCookiesAsync();
        else if (_pivot.SelectedIndex == 2) await LoadSourceAsync();
    }

    // ── Network ──

    private UIElement BuildNetwork()
    {
        _netList.ItemsSource = _net;
        _netList.SelectionChanged += (_, _) => ShowDetails();
        _filter.TextChanged += (_, _) => RefreshNetwork();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Btn("Copy as cURL", () => { if (Selected() is { } r) Copy(CurlOf(r)); }));
        actions.Children.Add(Btn("cURL → Code", () => { if (Selected() is { } r) (App.MainWindow as MainWindow)?.OpenTool("curl", CurlOf(r)); }));
        actions.Children.Add(Btn("HTTP Track", () => { if (Selected() is { } r) (App.MainWindow as MainWindow)?.OpenTool("httptrack", r.Url); }));
        actions.Children.Add(Btn("Clear", () => { _current?.Log.Clear(); RefreshNetwork(); }));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_filter);
        panel.Children.Add(_netList);
        panel.Children.Add(actions);
        panel.Children.Add(_details);
        return panel;
    }

    private NetRow? Selected() => _netList.SelectedItem as NetRow;

    private static NetRow FromEvent(CoreWebView2WebResourceResponseReceivedEventArgs a)
    {
        var row = new NetRow
        {
            Method = a.Request.Method,
            Url = a.Request.Uri,
            Status = a.Response.StatusCode,
            Reason = a.Response.ReasonPhrase ?? "",
            RequestHeaders = HeaderText(a.Request.Headers),
            ResponseHeaders = HeaderText(a.Response.Headers),
        };
        foreach (var header in a.Response.Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) row.ContentType = header.Value;
            else if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(header.Value, out var length)) row.Length = length;
        }
        return row;
    }

    private static string HeaderText(IEnumerable<KeyValuePair<string, string>> headers)
        => string.Join("\n", headers.Select(h => $"{h.Key}: {h.Value}"));

    private bool Matches(NetRow row)
    {
        var f = _filter.Text.Trim();
        if (f.Length == 0) return true;
        return row.Url.Contains(f, StringComparison.OrdinalIgnoreCase) || row.Method.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.Status.ToString().Contains(f) || row.ContentType.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void AddRow(BrowserTab tab, NetRow row)
    {
        tab.Log.Add(row);
        if (tab.Log.Count > MaxRows) tab.Log.RemoveAt(0);
        if (tab != _current || !Matches(row)) return;
        _net.Add(row);
        if (_net.Count > MaxRows) _net.RemoveAt(0);
    }

    private void RefreshNetwork()
    {
        _net.Clear();
        _details.Text = "";
        if (_current == null) return;
        foreach (var row in _current.Log.Where(Matches).TakeLast(MaxRows)) _net.Add(row);
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
            var core = _current?.View.CoreWebView2;
            if (core == null) return;
            foreach (var cookie in _cookies) core.CookieManager.DeleteCookie(cookie);
            _ = LoadCookiesAsync();
        }));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Cookies of the page that is open.", Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(actions);
        panel.Children.Add(_cookieList);
        return panel;
    }

    private async Task LoadCookiesAsync()
    {
        var core = _current?.View.CoreWebView2;
        if (core == null) return;
        try
        {
            var found = await core.CookieManager.GetCookiesAsync(core.Source);
            _cookies = found.ToList();
            _cookieList.ItemsSource = _cookies.Select(c =>
                $"{c.Name} = {Shorten(c.Value, 60)}\n{c.Domain}{c.Path}  ·  {(c.IsSession ? "session" : c.Expires.ToString("yyyy-MM-dd HH:mm"))}"
                + (c.IsSecure ? "  ·  Secure" : "") + (c.IsHttpOnly ? "  ·  HttpOnly" : "")).ToList();
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
    }

    // ── Source ──

    private UIElement BuildSource()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Btn("Reload source", () => _ = LoadSourceAsync()));
        actions.Children.Add(Btn("Copy", () => Copy(_source.Text)));

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "The page as it is now (after scripts ran).", Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(actions);
        panel.Children.Add(_source);
        return panel;
    }

    private async Task LoadSourceAsync()
    {
        var core = _current?.View.CoreWebView2;
        if (core == null) return;
        try
        {
            var json = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = JsonSerializer.Deserialize<string>(json) ?? "";
            _source.Text = html.Length > 400_000 ? html[..400_000] + "\n… (cut: the page is longer)" : html;
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
    }
}
