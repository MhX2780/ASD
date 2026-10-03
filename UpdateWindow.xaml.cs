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
        Title = "ASD Update";

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
        RootGrid.Loaded += async (_, _) => await RunAsync();
    }

    // ───────── window frame ─────────

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetupWindowFrame()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            int w = (int)(520 * scale), h = (int)(460 * scale);
            AppWindow.Resize(new SizeInt32(w, h));

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));

            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = false;
                p.IsMaximizable = false;
            }
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
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
