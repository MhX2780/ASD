using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace ASD;

/// <summary>
/// The Extensions window (opened from the navigation bar). Built in code, like the other secondary windows:
///   loading (progress, like the update window)  →  list (tabs Available / New / Updates / Installed + search)
///   →  one extension (Back, Install)  →  install steps (newer version? license, place, needs, confirm, install).
/// </summary>
internal sealed class ExtensionWindow : Window
{
    private static ExtensionWindow? _instance;

    /// <summary>Opens the window of one extension (details, install, update, remove). One such window at a time.</summary>
    public static void ShowFor(ExtInfo e, List<ExtInfo> all, Action<List<ExtInfo>> changed)
    {
        _instance?.Close();
        _instance = new ExtensionWindow(e, all, changed);
        _instance.Activate();
    }

    private enum Visual { Ring, Logo, Success, Error }

    private readonly CancellationTokenSource _cts = new();
    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false };

    // the "status" view (progress ring / icon, text, progress bar, buttons): used while loading, installing and for errors
    private readonly StackPanel _statusView = new() { Spacing = 16, Margin = new Thickness(36, 0, 36, 24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressRing _ring = new() { IsActive = true, Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _icon = new() { FontSize = 64, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460, IsTextSelectionEnabled = true };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, MaxWidth = 420, Visibility = Visibility.Collapsed };
    private readonly TextBlock _stats = new() { Opacity = 0.7, FontSize = 12, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _retry = new() { Content = "Try again", Visibility = Visibility.Collapsed };
    private readonly Button _toList = new() { Content = "Back to the extension", Visibility = Visibility.Collapsed };
    private readonly Button _restart = new() { Content = "Restart ASD now", Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Close", Visibility = Visibility.Collapsed };

    private readonly ExtInfo _e;
    private readonly Action<List<ExtInfo>> _changed;
    private List<ExtInfo> _all;
    private bool _busy;                   // an install is running: the window must not start another job

    private ExtensionWindow(ExtInfo e, List<ExtInfo> all, Action<List<ExtInfo>> changed)
    {
        _e = e; _all = all; _changed = changed;
        Title = e.Name;
        SystemBackdrop = new MicaBackdrop();
        Loc.Track(_root);   // the language chosen in Settings
        AppWindow.Title = e.Name;

        _root.RequestedTheme = SettingsStore.Get("ThemeMode", "auto") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new TitleBar
        {
            Title = e.Name, Background = new SolidColorBrush(Colors.Transparent),
            IsBackButtonVisible = false, IsPaneToggleButtonVisible = false,
        };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(_body, 1);
        _root.Children.Add(titleBar);
        _root.Children.Add(_body);
        Content = _root;

        BuildStatusView();
        SetupWindowFrame(titleBar);

        Closed += (_, _) =>
        {
            _cts.Cancel();
            if (ReferenceEquals(_instance, this)) _instance = null;
            try { _changed(_all); } catch { /* the page may be gone */ }
            _ = (App.MainWindow as MainWindow)?.RefreshExtensionsBadgeAsync(force: true);     // the number may have changed
        };
        _root.Loaded += (_, _) =>
        {
            CursorHelper.ApplyPointer(_root);
            ShowDetails(_e);
        };
    }

    // ───────── window frame (same approach as the update window) ─────────

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    private const int GWLP_HWNDPARENT = -8;

    private void SetupWindowFrame(TitleBar titleBar)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBar);
            WindowChrome.SetTitleIcon(titleBar);   // the ASD icon to the left of the title
            try { if (AppWindowTitleBar.IsCustomizationSupported()) AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall; } catch { }
            _root.ActualThemeChanged += (_, _) => WindowChrome.ApplyCaptionColors(AppWindow, _root.ActualTheme);
            WindowChrome.ApplyCaptionColors(AppWindow, _root.ActualTheme);

            var main = App.MainWindow;
            if (main != null) SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, WinRT.Interop.WindowNative.GetWindowHandle(main));

            double scale = GetDpiForWindow(hwnd) / 96.0;
            int w = (int)(640 * scale), h = (int)(600 * scale);
            AppWindow.Resize(new SizeInt32(w, h));

            RectInt32 area;
            var mainApp = main?.AppWindow;
            bool minimized = mainApp == null || (mainApp.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Minimized;
            area = !minimized
                ? new RectInt32(mainApp!.Position.X, mainApp.Position.Y, mainApp.Size.Width, mainApp.Size.Height)
                : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(Math.Max(0, area.X + (area.Width - w) / 2), Math.Max(0, area.Y + (area.Height - h) / 2)));

            if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMaximizable = false; p.IsMinimizable = false; }
            CursorHelper.HookWindowFrame(hwnd);
        }
        catch { /* cosmetic only */ }
    }

    // ───────── small helpers ─────────

    private static Windows.UI.Color AccentColor()
    {
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
        catch { return Colors.DodgerBlue; }
    }

    private Brush AccentBrush() => new SolidColorBrush(AccentColor());
    private Brush TextBrush() => new SolidColorBrush(_root.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White);

    private static Style? AccentStyle()
        => Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) ? s as Style : null;

    private Button Primary(string text, RoutedEventHandler click, bool enabled = true)
    {
        var b = new Button { Content = text, IsEnabled = enabled, Style = AccentStyle() };
        b.Click += click;
        return b;
    }

    private static TextBlock Text(string text, double size = 14, bool bold = false, double opacity = 1, bool wrap = true)
        => new()
        {
            Text = text, FontSize = size, Opacity = opacity, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };

    private Border Tag(string text, bool accent)
        => new()
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = accent ? AccentBrush() : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = accent ? new SolidColorBrush(Colors.White) : new SolidColorBrush(_root.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White) },
        };

    /// <summary>The extension's picture: its icon.png, else its Simple Icons logo, else its glyph.</summary>
    private FrameworkElement IconOf(ExtInfo e, double box)
    {
        FrameworkElement? inner = null;
        try
        {
            if (e.IconPath != null)
                inner = new Image { Width = box * 0.7, Height = box * 0.7, Source = new BitmapImage(new Uri(e.IconPath)) };
            else if (e.SimpleIcon.Length > 0)
                inner = SimpleIcons.Create(e.SimpleIcon, box * 0.5, TextBrush());
        }
        catch { inner = null; }
        inner ??= new FontIcon { Glyph = e.Glyph, FontSize = box * 0.5 };
        return new Border
        {
            Width = box, Height = box, CornerRadius = new CornerRadius(box / 5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Grid { Children = { inner } },
        };
    }

    private void Cursors() => CursorHelper.ApplyHandCursorToButtons(_root);

    private void Show(UIElement view)
    {
        _body.Content = view;
        Cursors();
    }

    // ───────── the status view (loading, installing, errors) ─────────

    private void BuildStatusView()
    {
        var grid = new Grid { Height = 112, Width = 112, HorizontalAlignment = HorizontalAlignment.Center, Children = { _ring, _icon } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8, Children = { _restart, _retry, _toList, _close } };
        _retry.Style = AccentStyle();
        _toList.Style = AccentStyle();
        _restart.Style = AccentStyle();
        _restart.Click += (_, _) => ExtensionLoader.RestartApp();
        _statusView.Children.Add(grid);
        _statusView.Children.Add(_title);
        _statusView.Children.Add(_detail);
        _statusView.Children.Add(_bar);
        _statusView.Children.Add(_stats);
        _statusView.Children.Add(buttons);
        _retry.Click += (_, _) => ShowDetails(_e);
        _toList.Click += (_, _) => ShowDetails(_e);
        _close.Click += (_, _) => Close();
    }

    private void ShowStatus(Visual visual, string title, string detail, bool bar = false, bool indeterminate = false, bool retry = false, bool close = false, bool toList = false, bool restart = false)
    {
        _restart.Visibility = restart ? Visibility.Visible : Visibility.Collapsed;
        _close.Content = restart ? "Restart later" : "Close";
        _ring.IsActive = visual == Visual.Ring;
        _ring.Visibility = visual == Visual.Ring ? Visibility.Visible : Visibility.Collapsed;
        _icon.Visibility = visual == Visual.Ring ? Visibility.Collapsed : Visibility.Visible;
        switch (visual)
        {
            case Visual.Success: _icon.Glyph = ""; _icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1E, 0xA8, 0x4B)); break;
            case Visual.Error: _icon.Glyph = ""; _icon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x4A, 0x3C)); break;
            default: _icon.Glyph = ""; _icon.Foreground = AccentBrush(); break;
        }
        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bar.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = indeterminate;
        if (bar && indeterminate) _bar.Value = 0;
        _stats.Visibility = Visibility.Collapsed;
        _retry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        _close.Visibility = close ? Visibility.Visible : Visibility.Collapsed;
        _toList.Visibility = toList ? Visibility.Visible : Visibility.Collapsed;
        Show(_statusView);
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    private void OnProgress(ExtProgress p)
    {
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = p.Indeterminate;
        if (!p.Indeterminate) _bar.Value = p.Percent;
        switch (p.Stage)
        {
            case "download":
                _title.Text = "Downloading extensions…";
                _stats.Text = p.Total > 0 ? $"{Mb(p.Received)} / {Mb(p.Total)}  ·  {Mb((long)p.BytesPerSecond)}/s  ·  {p.Percent:0}%" : $"{Mb(p.Received)}  ·  {Mb((long)p.BytesPerSecond)}/s";
                _stats.Visibility = Visibility.Visible;
                break;
            case "extract":
                _title.Text = "Unpacking…";
                break;
            case "install":
                _stats.Text = $"{p.Received} / {p.Total} files";
                _stats.Visibility = Visibility.Visible;
                break;
        }
    }

    // ───────── one extension ─────────

    private void ShowDetails(ExtInfo e)
    {
        if (e.IsNew) { e.IsNew = false; ExtensionStore.MarkSeen(e.Id); }

        var page = new StackPanel { Spacing = 16, Margin = new Thickness(28, 0, 28, 24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };


        // header
        var head = new Grid { ColumnSpacing = 16 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(IconOf(e, 76));
        var hs = new StackPanel { Spacing = 4 };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        nameRow.Children.Add(Text(e.Name, 26, bold: true, wrap: false));
        if (e.HasUpdate) nameRow.Children.Add(Tag("Update", true));
        else if (e.Installed) nameRow.Children.Add(Tag("Installed", false));
        hs.Children.Add(nameRow);
        var who = new List<string>();
        if (e.Author.Length > 0) who.Add("by " + e.Author);
        who.Add("version " + (e.Version.Length > 0 ? e.Version : "?"));
        if (e.Installed && e.InstalledVersion.Length > 0 && e.InstalledVersion != e.Version) who.Add("installed: " + e.InstalledVersion);
        hs.Children.Add(Text(string.Join("  ·  ", who), 13, opacity: 0.7));
        hs.Children.Add(Text(e.Id, 12, opacity: 0.5));
        Grid.SetColumn(hs, 1);
        head.Children.Add(hs);
        page.Children.Add(head);

        if (e.Description.Length > 0) { var d = Text(e.Description, 15); d.IsTextSelectionEnabled = true; page.Children.Add(d); }

        // actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (e.PackageDir != null && (!e.Installed || e.HasUpdate))
            actions.Children.Add(Primary(e.HasUpdate ? "Update to v" + e.Version : "Install", async (_, _) => await RunWizardAsync(e)));
        else if (!e.Installed)
            actions.Children.Add(new TextBlock { Text = "The package is not on this computer yet: connect to the internet and refresh.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        if (e.Installed)
        {
            var remove = new Button { Content = "Remove" };
            var confirm = new Button { Content = "Yes, remove it", Style = AccentStyle() };
            var flyout = new Flyout { Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = $"Remove {e.Name} from this computer?", TextWrapping = TextWrapping.Wrap, MaxWidth = 260 }, confirm } } };
            remove.Flyout = flyout;
            confirm.Click += (_, _) =>
            {
                flyout.Hide();
                try
                {
                    var restartNeeded = ExtensionStore.Remove(e);
                    if (e.PackageDir == null) _all.Remove(e);     // it is not in the store any more
                    if (restartNeeded) ShowStatus(Visual.Success, $"{e.Name} will be removed", "It is running right now, so ASD finishes the removal when it restarts.", close: true, restart: true);
                    else Close();
                }
                catch (Exception ex) { ShowStatus(Visual.Error, "Could not remove it", ex.Message, close: true, toList: true); }
            };
            actions.Children.Add(remove);
        }
        page.Children.Add(actions);

        // where it appears (installed ones can be moved at any time)
        if (e.Installed)
        {
            var place = new ComboBox { Header = "Shown in", MinWidth = 240 };
            place.Items.Add("Navigation bar");
            place.Items.Add("More tools");
            place.SelectedIndex = ExtensionStore.PlacementOf(e) == "main" ? 0 : 1;
            place.SelectionChanged += (_, _) => ExtensionStore.SetPlacement(e.Id, place.SelectedIndex == 0 ? "main" : "more");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(place, "Shown in");
            page.Children.Add(place);
            page.Children.Add(Text("A change of place shows after ASD restarts.", 12, opacity: 0.6));

            // why it did not start (or that it is running now)
            if (e.LoadError != null)
            {
                var err = Text("Not started: " + e.LoadError, 13);
                err.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x4A, 0x3C));
                page.Children.Add(err);
                if (ExtensionLoader.IsDisabled(e.Id))
                {
                    var again = new Button { Content = "Switch it on again" };
                    again.Click += (_, _) => { ExtensionLoader.Enable(e.Id); ShowStatus(Visual.Success, "It is switched on", "It starts the next time ASD starts.", close: true, restart: true); };
                    page.Children.Add(again);
                }
            }
            else if (e.Running) page.Children.Add(Text("Running in this ASD.", 12, opacity: 0.6));
        }

        // needs
        var needs = ExtensionStore.CheckNeeds(e, _all);
        if (needs.Count > 0)
        {
            page.Children.Add(Text("Needs", 16, bold: true));
            page.Children.Add(NeedsList(needs));
        }

        // screenshots
        var shots = e.Screenshots;
        if (shots.Count > 0)
        {
            page.Children.Add(Text("Pictures", 16, bold: true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var s in shots)
            {
                try
                {
                    var path = s;
                    var thumb = new Button { Padding = new Thickness(0), Content = new Image { Height = 190, Source = new BitmapImage(new Uri(path)) } };
                    ToolTipService.SetToolTip(thumb, "Click to enlarge");
                    thumb.Click += (_, _) => ShowShot(e, path);
                    row.Children.Add(thumb);
                }
                catch { }
            }
            page.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Auto, Content = row });
        }

        Show(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page });
    }

    /// <summary>A picture at full size inside the window (Back returns to the extension).</summary>
    private void ShowShot(ExtInfo e, string path)
    {
        var grid = new Grid { Margin = new Thickness(28, 0, 28, 24), RowSpacing = 12 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var back = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE72B", FontSize = 14 }, new TextBlock { Text = "Back" } } }, HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => ShowDetails(e);
        var image = new Image { Stretch = Stretch.Uniform };
        try { image.Source = new BitmapImage(new Uri(path)); } catch { }
        Grid.SetRow(image, 1);
        grid.Children.Add(back);
        grid.Children.Add(image);
        Show(grid);
    }

    private static StackPanel NeedsList(IEnumerable<ExtensionStore.Need> needs)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var n in needs)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (glyph, color) = n.Ok ? ("", Windows.UI.Color.FromArgb(255, 0x1E, 0xA8, 0x4B))
                : n.Blocks ? ("", Windows.UI.Color.FromArgb(255, 0xE0, 0x4A, 0x3C))
                : ("", Windows.UI.Color.FromArgb(255, 0xE0, 0xA0, 0x20));
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var t = new TextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            list.Children.Add(row);
        }
        return list;
    }

    // ───────── the install steps ─────────

    /// <summary>One step of the install window. Returns 1 (Next), -1 (Back) or 0 (Cancel).</summary>
    private Task<int> AskStep(string stepText, string heading, UIElement content, string nextText, bool canBack, bool nextEnabled = true, Action<Button>? wire = null)
    {
        var tcs = new TaskCompletionSource<int>();
        var grid = new Grid { Margin = new Thickness(32, 0, 32, 24), RowSpacing = 12, MaxWidth = 760 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Text(stepText, 12, opacity: 0.65));
        var h = Text(heading, 24, bold: true); Grid.SetRow(h, 1); grid.Children.Add(h);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        Grid.SetRow(scroll, 2); grid.Children.Add(scroll);

        var back = new Button { Content = "Back", Visibility = canBack ? Visibility.Visible : Visibility.Collapsed };
        var cancel = new Button { Content = "Cancel" };
        var next = Primary(nextText, (_, _) => tcs.TrySetResult(1), nextEnabled);
        back.Click += (_, _) => tcs.TrySetResult(-1);
        cancel.Click += (_, _) => tcs.TrySetResult(0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { back, cancel, next } };
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        wire?.Invoke(next);
        Show(grid);
        return tcs.Task;
    }

    private async Task RunWizardAsync(ExtInfo e)
    {
        if (_busy) return;
        _busy = true;
        var ct = _cts.Token;
        try
        {
            // 1) is there a newer version of the package? (silent when offline)
            ShowStatus(Visual.Ring, "Checking for a newer version…", "");
            try
            {
                if (await ExtensionStore.ListChangedAsync(ct))
                {
                    ShowStatus(Visual.Logo, "A newer version is available", "Downloading it first…", bar: true, indeterminate: true);
                    _all = await ExtensionStore.RefreshAsync(new Progress<ExtProgress>(OnProgress), ct);
                    var again = _all.FirstOrDefault(x => x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
                    if (again == null) { ShowStatus(Visual.Error, "This extension is not in the store any more", "", close: true, toList: true); return; }
                    e = again;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* offline or GitHub is slow: go on with the package that is already here */ }

            if (e.PackageDir == null) { ShowStatus(Visual.Error, "The package is not on this computer", "Connect to the internet and press refresh.", close: true, toList: true); return; }

            var placement = ExtensionStore.PlacementOf(e);
            var needs = ExtensionStore.CheckNeeds(e, _all);
            var blocked = needs.Any(n => n.Blocks && !n.Ok);

            var steps = new List<(string Name, Func<int, int, Task<int>> Run)>();

            // 2) license (only when the extension has one)
            var licensePath = e.LicensePath;
            if (licensePath != null)
            {
                steps.Add(("License", (i, n) =>
                {
                    string text;
                    try { text = File.ReadAllText(licensePath); if (text.Length > 200_000) text = text[..200_000] + "\n…"; }
                    catch (Exception ex) { text = "The license file could not be read: " + ex.Message; }
                    var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200, MaxHeight = 360, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
                    var accept = new CheckBox { Content = "I accept this license" };
                    var panel = new StackPanel { Spacing = 12, Children = { box, accept } };
                    return AskStep($"Step {i} of {n}", "License", panel, "Next", i > 1, false, next => { accept.Checked += (_, _) => next.IsEnabled = true; accept.Unchecked += (_, _) => next.IsEnabled = false; });
                }));
            }

            // 3) where it goes
            steps.Add(("Place", (i, n) =>
            {
                var radios = new RadioButtons();
                radios.Items.Add("Navigation bar: its own item in the left bar");
                radios.Items.Add("More tools: listed with the other tools in the Toolbox");
                radios.SelectedIndex = placement == "main" ? 0 : 1;
                radios.SelectionChanged += (_, _) => placement = radios.SelectedIndex == 0 ? "main" : "more";
                var panel = new StackPanel { Spacing = 12, Children = { Text("Where should this extension appear?", 14, opacity: 0.85), radios, Text("You can move it later from its page.", 12, opacity: 0.6) } };
                return AskStep($"Step {i} of {n}", "Place to add", panel, "Next", i > 1);
            }));

            // 4) what it needs
            steps.Add(("Needs", (i, n) =>
            {
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(NeedsList(needs));
                if (blocked) panel.Children.Add(Text("Fix the red items first, then try again.", 13, bold: true));
                else if (needs.Any(x => !x.Ok)) panel.Children.Add(Text("The yellow items do not stop the install.", 13, opacity: 0.7));
                return AskStep($"Step {i} of {n}", "What it needs", panel, "Next", i > 1, !blocked);
            }));

            // 5) confirm
            steps.Add(("Confirm", (i, n) =>
            {
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(Text($"{e.Name}  v{e.Version}" + (e.Author.Length > 0 ? "  by " + e.Author : ""), 16, bold: true));
                panel.Children.Add(Text("Shown in: " + (placement == "main" ? "the navigation bar" : "More tools"), 14));
                panel.Children.Add(Text("Saved to: " + Path.Combine(ExtensionStore.InstalledRoot, e.Id), 12, opacity: 0.65));
                if (e.Installed) panel.Children.Add(Text($"This replaces the installed version ({e.InstalledVersion}).", 13, opacity: 0.8));
                return AskStep($"Step {i} of {n}", "Ready to install", panel, e.Installed ? "Update" : "Install", i > 1);
            }));

            var idx = 0;
            while (idx < steps.Count)
            {
                var r = await steps[idx].Run(idx + 1, steps.Count);
                if (r == 0) { ShowDetails(e); return; }
                idx = Math.Max(0, idx + r);
            }

            // 6) the install itself
            ShowStatus(Visual.Logo, $"Installing {e.Name}…", "Copying files", bar: true, indeterminate: false);
            _bar.Value = 0;
            await ExtensionStore.InstallAsync(e, placement, new Progress<ExtProgress>(OnProgress), ct);
            ShowStatus(Visual.Success, $"{e.Name} is installed",
                "Shown in: " + (placement == "main" ? "the navigation bar" : "More tools") + ". Restart ASD to start it.", close: true, restart: true);
        }
        catch (OperationCanceledException) { /* window closed */ }
        catch (Exception ex)
        {
            ShowStatus(Visual.Error, "The install failed", ex.Message, close: true, toList: true);
        }
        finally { _busy = false; }
    }
}
