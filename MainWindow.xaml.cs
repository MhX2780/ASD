using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed partial class MainWindow : Window
{
    private ToolInfo? _current;
    private bool _paletteOpen;
    private string _lastClip = "";
    private string? _clipTag, _clipText;

    public MainWindow()
    {
        this.InitializeComponent();

        this.AppWindow.Title = "ASD";

        // Start maximized (respects DPI, taskbar and window borders)
        try
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
        }
        catch { /* non-critical, window will show at default size */ }

        RootNav.PaneHeader = new Grid
        {
            Height = 48,
            Children =
            {
                new TextBlock
                {
                    Text = "ASD",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                }
            }
        };

        ApplyTheme(LoadSavedTheme());
        CursorHelper.Initialize(RootNav, Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors"));

        // Ctrl+K = tool palette
        var accel = new KeyboardAccelerator { Key = Windows.System.VirtualKey.K, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        accel.Invoked += (_, e) => { e.Handled = true; ShowPalette(); };
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
        RootNav.RequestedTheme = mode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private static string LoadSavedTheme() => SettingsStore.Get("ThemeMode", "auto");

    private void RootNav_Loaded(object sender, RoutedEventArgs e)
    {
        RootNav.IsPaneOpen = false;
        OpenTool("ico");
        CursorHelper.ApplyHandCursorToNavItems(RootNav);
    }

    private void RootNav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: string tag }) return;
        if (tag == "search") { ShowPalette(); return; }
        OpenTool(tag);
    }

    /// <summary>Shows a tool. Tools listed under "More" keep the "More" item highlighted.</summary>
    public void OpenTool(string tag, string? parameter = null)
    {
        var tool = ToolRegistry.Find(tag) ?? ToolRegistry.Find("encode")!;
        SelectNavItem(tool.NavTag);

        if (parameter == null && _current?.Tag == tool.Tag && ContentHost.Content != null) return;

        (ContentHost.Content as ToolPage)?.Deactivate();
        var page = tool.Create();
        _current = tool;
        ContentHost.Content = page;
        (page as ToolPage)?.Activate(parameter);
    }

    private void SelectNavItem(string navTag)
    {
        var item = RootNav.MenuItems.Concat(RootNav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag as string == navTag);
        if (item != null) RootNav.SelectedItem = item;
    }

    // ───────── Ctrl+K palette ─────────
    private async void ShowPalette()
    {
        if (_paletteOpen || RootNav.XamlRoot == null) return;
        _paletteOpen = true;
        try
        {
            var box = new TextBox { PlaceholderText = "Type a tool name…", IsSpellCheckEnabled = false };
            var list = new ListView { MaxHeight = 320, SelectionMode = ListViewSelectionMode.Single };
            var shown = new List<ToolInfo>();
            ToolInfo? chosen = null;

            void Refresh()
            {
                shown = ToolRegistry.Search(box.Text).ToList();
                list.ItemsSource = shown.Select(t => $"{t.Title}   —   {t.Description}").ToList();
                if (shown.Count > 0) list.SelectedIndex = 0;
            }

            Refresh();
            box.TextChanged += (_, _) => Refresh();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Down && list.SelectedIndex < shown.Count - 1) { list.SelectedIndex++; e.Handled = true; }
                else if (e.Key == Windows.System.VirtualKey.Up && list.SelectedIndex > 0) { list.SelectedIndex--; e.Handled = true; }
            };

            var dlg = new ContentDialog
            {
                XamlRoot = RootNav.XamlRoot,
                Title = "Go to tool",
                PrimaryButtonText = "Open",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel { Spacing = 8, Width = 480, Children = { box, list } },
            };
            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count) { chosen = shown[list.SelectedIndex]; dlg.Hide(); }
            };
            dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);

            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary && list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count)
                chosen = shown[list.SelectedIndex];
            if (chosen != null) OpenTool(chosen.Tag);
        }
        catch { /* palette is a convenience: never crash */ }
        finally { _paletteOpen = false; }
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
