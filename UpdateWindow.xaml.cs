using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ASD;

/// <summary>
/// The update window. Flow:
///   progress ring  →  "is the folder writable?"  →  "is there a newer version?"
///   →  Windows update icon + linear progress bar (download, extract)  →  restart through the .bat installer.
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private static UpdateWindow? _instance;

    private readonly CancellationTokenSource _cts = new();
    private bool _running;
    private bool _installing;

    private enum Visual { Ring, Logo, Success, Error }

    /// <summary>Opens the update window (or brings the open one to the front) and starts the update.</summary>
    public static void ShowWindow()
    {
        if (_instance != null) { _instance.Activate(); return; }
        _instance = new UpdateWindow();
        _instance.Activate();
    }

    public UpdateWindow()
    {
        InitializeComponent();
        AppWindow.Title = "ASD Update";

        RootGrid.RequestedTheme = SettingsStore.Get("ThemeMode", "auto") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        SetupWindowFrame();

        Closed += (_, _) =>
        {
            _cts.Cancel();
            _instance = null;
        };
        RootGrid.Loaded += async (_, _) =>
        {
            // Same cursors as the main window (pointer + hand on buttons), if the cursor files exist
            CursorHelper.ApplyPointer(RootGrid);
            CursorHelper.ApplyHandCursorToButtons(RootGrid);
            await RunAsync();
        };
    }

    // ───────── window frame ─────────

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    private const int GWLP_HWNDPARENT = -8;   // for a top-level window this sets its OWNER

    private void SetupWindowFrame()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // Our own transparent title bar (same approach as the main window): Mica covers it too
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                    AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            }
            catch { /* cosmetic only */ }
            RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionColors();
            UpdateCaptionColors();

            // Owned by the main window: always drawn above it, minimizes/restores with it
            var main = App.MainWindow;
            if (main != null)
                SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, WinRT.Interop.WindowNative.GetWindowHandle(main));

            double scale = GetDpiForWindow(hwnd) / 96.0;
            int w = (int)(520 * scale), h = (int)(460 * scale);
            AppWindow.Resize(new SizeInt32(w, h));

            // Centered over the main window (or over the screen if the main window is minimized)
            RectInt32 area;
            var mainApp = main?.AppWindow;
            bool mainMinimized = mainApp == null || (mainApp.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Minimized;
            if (!mainMinimized)
                area = new RectInt32(mainApp!.Position.X, mainApp.Position.Y, mainApp.Size.Width, mainApp.Size.Height);
            else
                area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));

            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
            }

            // Pointer cursor on the title bar, hand cursor on its buttons (same as the main window)
            CursorHelper.HookWindowFrame(hwnd);
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>Makes the minimize / close buttons transparent and matches them to the light or dark theme.</summary>
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

    // ───────── the update flow ─────────

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        var ct = _cts.Token;

        try
        {
            // 1. Can the installation folder be written to? (checked first, before anything is downloaded)
            Show(Visual.Ring, "Preparing update…", $"Checking {UpdateService.AppDir}");
            if (!await Task.Run(UpdateService.IsInstallFolderWritable, ct))
            {
                Show(Visual.Error, UpdateService.NotWritableMessage, $"Folder: {UpdateService.AppDir}", retry: true, close: true);
                return;
            }

            // 2. Is there a newer version?
            Show(Visual.Ring, "Checking for updates…", "");
            var check = await UpdateService.CheckAsync(ct);
            if (!check.Available)
            {
                Show(Visual.Success, "ASD is up to date", UpdateService.Describe(check.LocalText), close: true);
                return;
            }

            // 3. Download + extract (logo + linear progress bar)
            Show(Visual.Logo, "Downloading update…", UpdateService.Describe(check.RemoteText), bar: true, barIndeterminate: true);
            await UpdateService.DownloadAndExtractAsync(new Progress<UpdateProgress>(OnProgress), ct);

            // 4. Hand over to the .bat installer and close ASD so it can replace the files
            Show(Visual.Logo, "Restarting ASD…", "ASD will close and open again in a moment.", bar: true, barIndeterminate: true);
            _installing = true;
            await Task.Delay(700, ct);
            UpdateService.StartInstaller();
            Application.Current.Exit();
        }
        catch (OperationCanceledException)
        {
            // window closed while working
        }
        catch (Exception ex)
        {
            _installing = false;
            Show(Visual.Error, "Update Failed", ex.Message, retry: true, close: true);
        }
        finally
        {
            _running = false;
        }
    }

    private void OnProgress(UpdateProgress p)
    {
        if (_installing) return;

        Bar.IsIndeterminate = p.Indeterminate;
        if (!p.Indeterminate) Bar.Value = p.Percent;

        if (p.Stage == "extract")
        {
            TitleText.Text = "Extracting files…";
            StatsText.Text = $"{p.Percent:0}%";
        }
        else
        {
            TitleText.Text = "Downloading update…";
            StatsText.Text = p.Total > 0
                ? $"{Mb(p.Received)} / {Mb(p.Total)}  ·  {Mb((long)p.BytesPerSecond)}/s  ·  {p.Percent:0}%"
                : $"{Mb(p.Received)}  ·  {Mb((long)p.BytesPerSecond)}/s";
        }
        StatsText.Visibility = Visibility.Visible;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    // ───────── view helper ─────────

    private void Show(Visual visual, string title, string detail,
        bool bar = false, bool barIndeterminate = false, bool retry = false, bool close = false)
    {
        Ring.IsActive = visual == Visual.Ring;
        Ring.Visibility = visual == Visual.Ring ? Visibility.Visible : Visibility.Collapsed;

        // Built-in Windows (Segoe Fluent / MDL2) icons: update arrows, check mark, warning
        bool icon = visual != Visual.Ring;
        StatusIcon.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;
        if (icon)
        {
            string glyph;
            int r, g, b;
            switch (visual)
            {
                case Visual.Success: glyph = "\uE930"; (r, g, b) = (0x1E, 0xA8, 0x4B); break;   // green check
                case Visual.Error:   glyph = "\uE7BA"; (r, g, b) = (0xE0, 0x4A, 0x3C); break;   // red warning
                default:             glyph = "\uE777"; (r, g, b) = (0x3B, 0x8E, 0xEA); break;   // blue Windows "update" icon
            }
            StatusIcon.Glyph = glyph;
            StatusIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b));
        }

        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Bar.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        Bar.IsIndeterminate = barIndeterminate;
        if (bar && barIndeterminate) Bar.Value = 0;
        StatsText.Visibility = Visibility.Collapsed;

        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = close ? Visibility.Visible : Visibility.Collapsed;
        CursorHelper.ApplyHandCursorToButtons(RootGrid);
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
