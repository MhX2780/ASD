using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed partial class MainWindow : Window
{
    private ToolInfo? _current;
    private Stack<string> _history = new();
    private Dictionary<string, ToolInfo> _suggest = new();
    private string _lastClip = "";
    private string? _clipTag, _clipText;

    public MainWindow()
    {
        this.InitializeComponent();

        this.AppWindow.Title = "ASD";
        this.ExtendsContentIntoTitleBar = true;   // our own TitleBar replaces the default one
        this.SetTitleBar(AppTitleBar);

        // Start maximized (respects DPI, taskbar and window borders)
        try
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
        }
        catch { /* non-critical, window will show at default size */ }

        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        ApplyTheme(LoadSavedTheme());
        CursorHelper.Initialize(RootNav, Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors"));

        // Ctrl+K = tool palette
        var accel = new KeyboardAccelerator { Key = Windows.System.VirtualKey.K, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        accel.Invoked += (_, e) => { e.Handled = true; FocusSearch(); };
        RootNav.KeyboardAccelerators.Add(accel);

        // Clipboard banner
        var openBtn = new Button { Content = "Open" };
        openBtn.Click += OnClipOpen;
        ClipBar.ActionButton = openBtn;
        this.Activated += OnWindowActivated;

        // Save the inputs of the visible tool when the window closes
        this.Closed += (_, _) => (ContentHost.Content as ToolPage)?.Deactivate();
    }

    public void ApplyTheme(string mode)
    {
        RootGrid.RequestedTheme = mode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionColors();
    }

    /// <summary>Makes the minimize / maximize / close buttons match the light or dark theme.</summary>
    private void UpdateCaptionColors()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var light = RootGrid.ActualTheme == ElementTheme.Light;
            var fg = light ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var hover = light ? Windows.UI.Color.FromArgb(25, 0, 0, 0) : Windows.UI.Color.FromArgb(25, 255, 255, 255);
            var none = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = none;
            tb.ButtonInactiveBackgroundColor = none;
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
        }
        catch { /* cosmetic only */ }
    }

    private static string LoadSavedTheme() => SettingsStore.Get("ThemeMode", "auto");

    private void RootNav_Loaded(object sender, RoutedEventArgs e)
    {
        RootNav.IsPaneOpen = false;
        UpdateCaptionColors();
        OpenTool("ico");
        CursorHelper.ApplyHandCursorToNavItems(RootNav);
    }

    private void RootNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: string tag }) return;
        if (tag == "search") { FocusSearch(); return; }
        OpenTool(tag);
    }

    /// <summary>Shows a tool. Tools listed under "More" keep the "More" item highlighted.</summary>
    public void OpenTool(string tag, string? parameter = null) => Navigate(tag, parameter, record: true);

    private void Navigate(string tag, string? parameter, bool record)
    {
        var tool = ToolRegistry.Find(tag) ?? ToolRegistry.Find("encode")!;
        SelectNavItem(tool.NavTag);

        if (parameter == null && _current?.Tag == tool.Tag && ContentHost.Content != null) return;

        if (record && _current != null)
        {
            _history.Push(_current.Tag);
            if (_history.Count > 50) _history = new Stack<string>(_history.Take(50).Reverse());
        }

        (ContentHost.Content as ToolPage)?.Deactivate();
        var page = tool.Create();
        _current = tool;
        ContentHost.Content = page;
        (page as ToolPage)?.Activate(parameter);
        AppTitleBar.IsBackButtonVisible = _history.Count > 0;
    }

    // ───────── title bar: back, pane toggle, search ─────────
    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (_history.Count > 0) Navigate(_history.Pop(), null, record: false);
        AppTitleBar.IsBackButtonVisible = _history.Count > 0;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
        => RootNav.IsPaneOpen = !RootNav.IsPaneOpen;

    private void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void FillSuggestions(string? query)
    {
        _suggest = ToolRegistry.Search(query).Take(8).ToDictionary(t => $"{t.Title}   —   {t.Description}", t => t);
        SearchBox.ItemsSource = _suggest.Keys.ToList();
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        FillSuggestions(SearchBox.Text);
        SearchBox.IsSuggestionListOpen = true;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        FillSuggestions(sender.Text);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ToolInfo? tool = null;
        if (args.ChosenSuggestion is string s && _suggest.TryGetValue(s, out var t)) tool = t;
        else tool = ToolRegistry.Search(args.QueryText).FirstOrDefault();

        sender.Text = "";
        sender.ItemsSource = null;
        if (tool != null) OpenTool(tool.Tag);
    }

    private void SelectNavItem(string navTag)
    {
        var item = RootNav.MenuItems.Concat(RootNav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag as string == navTag);
        if (item != null) RootNav.SelectedItem = item;
    }

    // ───────── clipboard banner ─────────
    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        if (SettingsStore.Get("ClipboardDetect", "on") != "on") return;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;
            var text = (await view.GetTextAsync())?.Trim();
            if (string.IsNullOrEmpty(text) || text == _lastClip) return;
            _lastClip = text;

            var hit = ClipboardDetector.Detect(text);
            if (hit == null) return;
            var (tag, label) = hit.Value;
            if (_current?.Tag == tag) return;

            _clipTag = tag;
            _clipText = text;
            ClipBar.Title = label;
            ClipBar.Message = "Open it in the matching tool?";
            ClipBar.IsOpen = true;
        }
        catch { /* clipboard can be locked by another app */ }
    }

    private void OnClipOpen(object sender, RoutedEventArgs e)
    {
        ClipBar.IsOpen = false;
        if (_clipTag != null) OpenTool(_clipTag, _clipText);
    }
}
