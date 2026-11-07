using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ASD;

/// <summary>One request the page made (from WebView2's response event).</summary>
internal sealed class NetRow
{
    private static long _counter;
    public readonly long Id = Interlocked.Increment(ref _counter);   // grows with every request: tells the Inspect page which ones are new
    public string Method = "", Url = "", Reason = "", ContentType = "", RequestHeaders = "", ResponseHeaders = "";
    public int Status;
    public long Length = -1;

    public bool Matches(string filter)
    {
        if (filter.Length == 0) return true;
        return Url.Contains(filter, StringComparison.OrdinalIgnoreCase) || Method.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Status.ToString().Contains(filter) || ContentType.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public override string ToString()
    {
        var kind = ContentType.Split(';')[0].Split('/').LastOrDefault() ?? "";
        var size = Length < 0 ? "" : Length < 1024 ? $"{Length} B" : Length < 1048576 ? $"{Length / 1024.0:0.#} KB" : $"{Length / 1048576.0:0.#} MB";
        return $"{Status}   {Method}   {Cut(Url, 80)}   {kind}   {size}".TrimEnd();
    }
}

/// <summary>
/// The Web Browser, in its own window: tabs, an address / search bar, on top of Microsoft Edge's WebView2.
/// The tool page "Web Browser" (BrowserPage) inspects the page that is open here: network, cookies, source.
/// One window at a time; closing the last tab closes the window.
/// </summary>
internal sealed class BrowserWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static BrowserWindow? _instance;
    public static BrowserWindow? Current => _instance;

    /// <summary>Opens the window (or brings the open one to the front). With a web address, that address opens in a new tab.</summary>
    public static BrowserWindow ShowWindow(string? url = null)
    {
        _instance ??= new BrowserWindow();
        _instance.Open(url);
        _instance.Activate();
        return _instance;
    }

    private const string HomeUrl = "https://duckduckgo.com/";
    private const int MaxRows = 3000;
    private static CoreWebView2Environment? _environment;

    private sealed class Tab
    {
        public readonly WebView2 View = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Visibility = Visibility.Collapsed };
        public TabViewItem Item = new();
        public readonly List<NetRow> Log = new();
        public bool Loading;
    }

    // what the Inspect page listens to (all raised on the UI thread)
    public event Action? NetworkChanged;   // a request was added, or the list was cleared
    public event Action? CurrentChanged;   // another tab, or the page changed
    public event Action? WindowClosed;

    public CoreWebView2? Core => _current?.View.CoreWebView2;
    public IReadOnlyList<NetRow> Log => (IReadOnlyList<NetRow>?)_current?.Log ?? Array.Empty<NetRow>();
    public string PageTitle => Core?.DocumentTitle ?? "";
    public string PageUrl => Core?.Source ?? "";

    public void ClearLog()
    {
        _current?.Log.Clear();
        NetworkChanged?.Invoke();
    }

    private readonly List<Tab> _tabs = new();
    private Tab? _current;
    private bool _ready;
    private string? _pending;

    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Grid _area = new() { RowSpacing = 6, Margin = new Thickness(12, 0, 12, 12) };
    private readonly TabView _tabView = new()
    {
        IsAddTabButtonVisible = true,
        TabWidthMode = TabViewWidthMode.SizeToContent,
        CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly Grid _views = new();   // every tab's WebView2; only the selected one is visible
    private readonly TextBox _address = new() { PlaceholderText = "Search or enter a web address", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly Button _back, _forward, _reload;

    private BrowserWindow()
    {
        Title = "Web Browser";
        AppWindow.Title = "Web Browser";
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* an older Windows: plain background */ }
        WindowChrome.ApplyTaskbarIcon(AppWindow);

        _root.RequestedTheme = SettingsStore.Get("ThemeMode", "auto") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        _back = IconButton("", "Back", () => _current?.View.CoreWebView2?.GoBack());
        _forward = IconButton("", "Forward", () => _current?.View.CoreWebView2?.GoForward());
        _reload = IconButton("", "Reload", ReloadOrStop);
        var devTools = IconButton("", "DevTools: Edge's full developer tools for this page", () => _current?.View.CoreWebView2?.OpenDevToolsWindow());

        _address.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            Go(_address.Text);
            e.Handled = true;
        };
        _address.GotFocus += (_, _) => _address.SelectAll();

        // toolbar
        var toolbar = new Grid { ColumnSpacing = 6 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Place(toolbar, _back, 0); Place(toolbar, _forward, 1); Place(toolbar, _reload, 2);
        Place(toolbar, _address, 3); Place(toolbar, devTools, 4);

        // tabs: only the strip is used; the pages live in _views
        _tabView.AddTabButtonClick += (_, _) => NewTab(HomeUrl);
        _tabView.TabCloseRequested += (_, a) => { if (a.Tab.Tag is Tab t) CloseTab(t); };
        _tabView.SelectionChanged += (_, _) => SwitchTab(_tabView.SelectedItem is TabViewItem { Tag: Tab t } ? t : null);

        _area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _area.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_info, 0); Grid.SetRow(toolbar, 1); Grid.SetRow(_tabView, 2); Grid.SetRow(_views, 3);
        _area.Children.Add(_info);
        _area.Children.Add(toolbar);
        _area.Children.Add(_tabView);
        _area.Children.Add(_views);

        var titleBar = new TitleBar
        {
            Title = "Web Browser",
            Background = new SolidColorBrush(Colors.Transparent),
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
        };
        WindowChrome.SetTitleIcon(titleBar);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(_area, 1);
        _root.Children.Add(titleBar);
        _root.Children.Add(_area);
        Content = _root;

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBar);
            if (AppWindowTitleBar.IsCustomizationSupported())
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch { /* cosmetic only */ }
        _root.ActualThemeChanged += (_, _) => WindowChrome.ApplyCaptionColors(AppWindow, _root.ActualTheme);
        WindowChrome.ApplyCaptionColors(AppWindow, _root.ActualTheme);

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            int w = (int)Math.Min(1200 * scale, area.Width * 0.92), h = (int)Math.Min(800 * scale, area.Height * 0.92);
            AppWindow.Resize(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
            CursorHelper.HookWindowFrame(hwnd);   // pointer on the title bar, double-click maximizes
        }
        catch { /* cosmetic only */ }

        _root.Loaded += (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            CursorHelper.ApplyHandCursorToButtons(_root);
            Open(_pending);
        };

        Closed += (_, _) =>
        {
            foreach (var t in _tabs) { try { t.View.Close(); } catch { } }
            _tabs.Clear();
            _current = null;
            if (ReferenceEquals(_instance, this)) _instance = null;
            WindowClosed?.Invoke();
        };
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

    /// <summary>The first tab (or a new tab for the given address). Waits until the window is on screen, because WebView2 needs that.</summary>
    private void Open(string? url)
    {
        if (!_ready) { _pending = url ?? _pending; return; }
        if (_tabs.Count == 0) NewTab(string.IsNullOrWhiteSpace(url) ? HomeUrl : url);
        else if (!string.IsNullOrWhiteSpace(url)) NewTab(url);
    }

    // ───────── tabs ─────────

    private void NewTab(string url)
    {
        var tab = new Tab();
        tab.Item = new TabViewItem { Header = "New tab", Tag = tab, IconSource = new SymbolIconSource { Symbol = Symbol.Globe } };
        _tabs.Add(tab);
        _views.Children.Add(tab.View);
        _tabView.TabItems.Add(tab.Item);
        _tabView.SelectedItem = tab.Item;
        _ = InitTabAsync(tab, url);
    }

    private void CloseTab(Tab tab)
    {
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        _tabView.TabItems.Remove(tab.Item);
        _views.Children.Remove(tab.View);
        try { tab.View.Close(); } catch { }

        if (_tabs.Count == 0) { _current = null; Close(); return; }
        if (_current == tab || _current == null)
            _tabView.SelectedItem = _tabs[Math.Clamp(index, 0, _tabs.Count - 1)].Item;
    }

    private void SwitchTab(Tab? tab)
    {
        _current = tab;
        foreach (var t in _tabs) t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolbar();
        CurrentChanged?.Invoke();
    }

    private void UpdateToolbar()
    {
        var core = _current?.View.CoreWebView2;
        _back.IsEnabled = core?.CanGoBack == true;
        _forward.IsEnabled = core?.CanGoForward == true;
        _address.Text = core?.Source is { Length: > 0 } s && s != "about:blank" ? s : "";
        ((FontIcon)_reload.Content).Glyph = _current?.Loading == true ? "" : "";
    }

    private async Task InitTabAsync(Tab tab, string url)
    {
        try
        {
            _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, EnsureProfileFolder(), new CoreWebView2EnvironmentOptions());
            await tab.View.EnsureCoreWebView2Async(_environment);
            var core = tab.View.CoreWebView2;

            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Item.Header = Shorten(string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New tab" : core.DocumentTitle, 26);
                if (tab == _current) CurrentChanged?.Invoke();
            };
            core.SourceChanged += (_, _) => { if (tab == _current) UpdateToolbar(); };
            core.HistoryChanged += (_, _) => { if (tab == _current) UpdateToolbar(); };
            core.NavigationStarting += (_, _) => { tab.Loading = true; if (tab == _current) UpdateToolbar(); };
            core.NavigationCompleted += (_, _) =>
            {
                tab.Loading = false;
                if (tab != _current) return;
                UpdateToolbar();
                CurrentChanged?.Invoke();
            };
            core.NewWindowRequested += (_, a) => { a.Handled = true; NewTab(a.Uri); };   // links that open a new window become a new tab
            core.WebResourceResponseReceived += (_, a) =>
            {
                try { AddRow(tab, FromEvent(a)); } catch { /* one odd request must not stop the log */ }
            };

            core.Navigate(ToUrl(url));
            UpdateToolbar();
            if (tab == _current) CurrentChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _info.Message = ex.GetType().Name.Contains("NotFound", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase)
                ? "Web Browser needs the Microsoft Edge WebView2 Runtime, which was not found. Install it from https://developer.microsoft.com/microsoft-edge/webview2 and open this window again."
                : "The browser could not start. " + ex.Message;
            _info.IsOpen = true;
        }
    }

    /// <summary>The browser's own profile (cookies, logins, cache), next to ASD's other data.</summary>
    private static string EnsureProfileFolder()
    {
        var folder = AppPaths.File("browser");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void AddRow(Tab tab, NetRow row)
    {
        tab.Log.Add(row);
        if (tab.Log.Count > MaxRows) tab.Log.RemoveAt(0);
        if (tab == _current) NetworkChanged?.Invoke();
    }

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

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
