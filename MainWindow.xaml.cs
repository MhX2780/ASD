using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
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
        WindowChrome.ApplyTaskbarIcon(AppWindow);   // app.ico on the taskbar button
        this.ExtendsContentIntoTitleBar = true;   // our own TitleBar replaces the default one
        this.SetTitleBar(AppTitleBar);

        // Tall title bar (48 px): makes the system minimize / maximize / close buttons
        // as tall as our toolbar instead of the default 32 px.
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch { /* cosmetic only */ }

        // Start maximized (respects DPI, taskbar and window borders)
        try
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
        }
        catch { /* non-critical, window will show at default size */ }

        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        ApplyTheme(LoadSavedTheme());
        // RootGrid (not RootNav) so the title bar is covered too, not just the navigation area.
        CursorHelper.Initialize(RootGrid, Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors"));
        CursorHelper.HookWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(this));   // drag area + min/max/close
        AppTitleBar.Loaded += (_, _) => CursorHelper.ApplyHandCursorToButtons(AppTitleBar); // back + pane-toggle buttons

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
        CursorHelper.SetDarkTheme(RootGrid.ActualTheme == ElementTheme.Dark);
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
        OpenTool("home");
        CursorHelper.ApplyHandCursorToNavItems(RootNav);
        _splash = PlaySplashAsync();
        _ = CheckForUpdateOnStartupAsync();
    }

    private void RootNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: string tag }) return;
        if (tag == "search") { FocusSearch(); return; }
        if (tag == "updates") { UpdateWindow.ShowWindow(); return; }   // opens its own window, no page
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

    // ───────── updates ─────────
    private bool _updatePrompted;

    /// <summary>
    /// On every start: ask GitHub if a newer version exists. If so, put a dot on the "Updates"
    /// button and show a dialog (Update now / Update later). Silent when offline.
    /// Builds without an update.txt (run from Visual Studio) are never prompted.
    /// </summary>
    private async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            if (_updatePrompted || UpdateService.ReadLocal() == null) return;

            var check = await UpdateService.CheckAsync(CancellationToken.None);
            if (!check.Available) return;

            _updatePrompted = true;
            UpdatesBadge.Visibility = Visibility.Visible;
            await _splash;   // never show the dialog on top of the splash

            var dialog = new ContentDialog
            {
                XamlRoot = RootNav.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Update available!",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "A new version of ASD is ready to install.", TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = UpdateService.Describe(check.RemoteText),
                            Opacity = 0.7,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        },
                    },
                },
                PrimaryButtonText = "Update now",          // accent (blue) button
                CloseButtonText = "Update later",          // normal button
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                UpdateWindow.ShowWindow();
        }
        catch
        {
            // offline, GitHub unreachable, another dialog already open ... never bother the user at startup
        }
    }

    // ───────── splash ─────────
    private Task _splash = Task.CompletedTask;

    /// <summary>
    /// Startup splash: the app icon fades in on a plain background, stays a moment, then the whole
    /// overlay fades out and the app is revealed. Never blocks or breaks the app: if the icon file is
    /// missing or anything fails, the overlay is simply removed.
    /// </summary>
    private async Task PlaySplashAsync()
    {
        try
        {
            var png = Path.Combine(AppContext.BaseDirectory, "Assets", "SplashIcon.png");
            if (!File.Exists(png)) return;

            SplashIcon.Source = new BitmapImage(new Uri(png));
            await FadeAsync(SplashIcon, 0, 1, 300);     // icon appears
            await Task.Delay(600);                      // stays
            await FadeAsync(SplashOverlay, 1, 0, 450);  // everything fades, app revealed
        }
        catch { /* cosmetic only */ }
        finally
        {
            SplashOverlay.Visibility = Visibility.Collapsed;   // also stops it from catching clicks
        }
    }

    private static Task FadeAsync(UIElement target, double from, double to, int milliseconds)
    {
        var done = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => done.TrySetResult();
        storyboard.Begin();
        return done.Task;
    }
}
