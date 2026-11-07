using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Graphics;
using WinRT.Interop;

namespace ASD.Setup;

/// <summary>
/// The whole installer: one window with a few views (welcome, progress, done, error), built in code like the other
/// windows in ASD. The title bar is ASD's own: a transparent TitleBar over Mica, tall, with theme-matched caption buttons.
/// </summary>
public sealed class SetupWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // window sizes in DIPs (outer size, title bar included): short, because each view only has a little to show
    private const int WindowWidth = 520;
    private const int HeightFirstInstall = 440;   // welcome page with the license
    private const int HeightSmall = 300;
    private const int HeightError = 400;

    private readonly CancellationTokenSource _cancel = new();
    private readonly Grid _root = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
    private readonly Grid _frame = new() { Margin = new Thickness(32, 4, 32, 22) };
    private readonly string _license = Installer.ReadText("LICENSE");
    private readonly byte[]? _certificate = Installer.ReadCertificate();
    private readonly bool _auto;   // --auto: the window does by itself what a person would click (for watching a test)

    // welcome view
    private TextBlock _status = new();
    private Button _install = new();
    private CheckBox? _accept;          // only on a first install

    // progress view
    private readonly TextBlock[] _stepMarks = new TextBlock[2];
    private readonly TextBlock[] _stepTexts = new TextBlock[2];
    private ProgressBar _bar = new();
    private TextBlock _detail = new();

    private bool _working;
    private bool _ready;
    private bool _placed;
    private string? _local;   // ASD.msix next to this installer

    private static readonly string[] StepNames =
    {
        "Getting ready to install",
        "Installing ASD",
    };

    public SetupWindow(bool auto = false)
    {
        _auto = auto;
        Title = "ASD Setup";
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* an older Windows: plain background */ }

        var titleBar = new TitleBar
        {
            Title = "ASD Setup",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(_frame, 1);
        _root.Children.Add(titleBar);
        _root.Children.Add(_frame);
        Content = _root;

        SetupFrame(titleBar);
        Closed += (_, _) => _cancel.Cancel();

        ShowLoading();
        _ = PrepareAsync();
    }

    // ───────── window frame (same approach as ASD's own windows) ─────────

    private void SetupFrame(TitleBar titleBar)
    {
        try
        {
            ExtendsContentIntoTitleBar = true;   // our own TitleBar replaces the default one
            SetTitleBar(titleBar);
            if (AppWindowTitleBar.IsCustomizationSupported())
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch { /* cosmetic only */ }

        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
                AppWindow.SetTaskbarIcon(icon);
            }
        }
        catch { }

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }
        }
        catch { }

        _root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();
    }

    /// <summary>Transparent min/close buttons whose icons match the light or dark theme (the Mica look).</summary>
    private void ApplyCaptionColors()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var light = _root.ActualTheme == ElementTheme.Light;
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

    /// <summary>Sets the window to the given height (in DIPs). The first time it is centered on the screen; later it keeps its center.</summary>
    private void SizeWindow(int heightDip)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
            int w = (int)(WindowWidth * scale), h = (int)(heightDip * scale);

            int cx, cy;
            if (_placed)
            {
                var pos = AppWindow.Position;
                var size = AppWindow.Size;
                cx = pos.X + size.Width / 2;
                cy = pos.Y + size.Height / 2;
            }
            else
            {
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
                cx = area.X + area.Width / 2;
                cy = area.Y + area.Height / 2;
                _placed = true;
            }
            AppWindow.Resize(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(cx - w / 2, cy - h / 2));
        }
        catch { }
    }

    // ───────── small building blocks ─────────

    private static TextBlock Text(string text, double size = 14, bool bold = false, bool dim = false)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Opacity = dim ? 0.7 : 1,
            TextWrapping = TextWrapping.Wrap,
        };

    /// <summary>
    /// One view: the header (logo + title) on top, the content under it, and the buttons pinned to the bottom right.
    /// It fills the width of the window (no fixed width, so nothing is cut off at the sides).
    /// </summary>
    private static Grid Page(FrameworkElement header, FrameworkElement? body, params Button[] buttons)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        if (body != null)
        {
            body.Margin = new Thickness(0, 18, 0, 0);
            body.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(body, 1);
            grid.Children.Add(body);
        }

        if (buttons.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            Grid.SetRow(row, 2);
            grid.Children.Add(row);
        }
        return grid;
    }

    private static StackPanel Stack(double spacing) => new() { Spacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static Button MakeButton(string label, string id, bool accent = false)
    {
        var button = new Button { Content = label, MinWidth = 110, Padding = new Thickness(18, 7, 18, 7) };
        AutomationProperties.SetAutomationId(button, id);   // so a test can find the button by name, not by position
        if (accent)
        {
            try { button.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; } catch { }
        }
        return button;
    }

    private static BitmapImage? _logo;

    /// <summary>The 256px logo inside this exe (a PNG: an .ico would be decoded as its smallest, blurry frame).</summary>
    private static BitmapImage? LoadLogo()
    {
        if (_logo != null) return _logo;
        try
        {
            var bytes = Installer.ReadBytes("logo.png");
            if (bytes == null) return null;
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var write = stream.AsStreamForWrite();
            write.Write(bytes, 0, bytes.Length);
            write.Flush();
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 128 };
            bitmap.SetSource(stream);
            return _logo = bitmap;
        }
        catch { return null; }
    }

    private static FrameworkElement Header(string title, string subtitle)
    {
        FrameworkElement logo;
        var bitmap = LoadLogo();
        logo = bitmap != null
            ? new Image { Source = bitmap, Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = "\uE74C", FontSize = 40 };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Text(title, 26, bold: true));
        texts.Children.Add(Text(subtitle, 13, dim: true));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(logo);
        row.Children.Add(texts);
        return row;
    }

    private void Show(FrameworkElement view, int heightDip)
    {
        _frame.Children.Clear();
        _frame.Children.Add(view);
        SizeWindow(heightDip);
    }

    // ───────── 0. looking at what is installed ─────────

    private void ShowLoading()
    {
        Show(new ProgressRing { IsActive = true, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, HeightSmall);
    }

    /// <summary>Finds out what is installed, whether an offline package is here, and what GitHub has; then shows the welcome page.</summary>
    private async Task PrepareAsync()
    {
        if (_certificate == null)
        {
            ShowError("This installer was built without ASD's certificate, so it cannot install ASD. Download the Setup from the ASD release page.", fatal: true);
            Installer.Log("FAILED: no certificate inside this installer");
            return;
        }

        var installed = await Task.Run(Installer.FindInstalled);
        _local = Installer.LocalPackage();
        ShowWelcome(installed);

        string line;
        Version? latest = null;
        if (_local != null)
            line = "Using the ASD.msix next to this installer (no download needed).";
        else
        {
            latest = await Installer.TryLatestVersionAsync();
            line = latest != null
                ? $"Latest version on GitHub: {latest}"
                : "GitHub could not be reached right now. Check your internet connection, then press Install.";
        }

        if (installed != null)
        {
            var have = Installer.VersionOf(installed);
            bool upToDate = latest != null && have >= latest;
            line = upToDate
                ? $"ASD {have} is installed and up to date. Install again to repair it."
                : $"ASD {have} is installed. This will update it.";
            if (latest != null && have < latest) line += $" (Latest: {latest})";
            _install.Content = upToDate ? "Repair" : "Update";
        }

        _status.Text = line;
        _ready = true;
        UpdateInstallEnabled();
        if (_auto) _ = AutoStartAsync();
    }

    // ───────── 1. welcome (the license only on a first install) ─────────

    private void ShowWelcome(Package? installed)
    {
        bool firstInstall = installed == null;

        var panel = Stack(12);
        var header = Header("ASD Setup", firstInstall ? "Installs ASD on this PC" : "ASD is already installed");

        _status = Text("Looking up the latest version…", 13, dim: true);
        panel.Children.Add(_status);

        if (firstInstall)
        {
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
                Child = new ScrollViewer
                {
                    Height = 120,
                    Padding = new Thickness(14, 10, 14, 10),
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(_license) ? "No license text is included." : _license,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            });

            _accept = new CheckBox { Content = "I accept the license" };
            AutomationProperties.SetAutomationId(_accept, "AcceptLicense");
            _accept.Checked += (_, _) => UpdateInstallEnabled();
            _accept.Unchecked += (_, _) => UpdateInstallEnabled();
            panel.Children.Add(_accept);
        }
        else
        {
            _accept = null;   // already accepted when it was first installed
        }

        _install = MakeButton("Install", "InstallButton", accent: true);
        _install.IsEnabled = false;
        _install.Click += async (_, _) => await RunInstallAsync();

        var close = MakeButton("Close", "CloseButton");
        close.Click += (_, _) => Close();

        Show(Page(header, panel, close, _install), firstInstall ? HeightFirstInstall : HeightSmall);
    }

    private void UpdateInstallEnabled()
        => _install.IsEnabled = _ready && !_working && (_accept == null || _accept.IsChecked == true);

    // ───────── --auto: do what a person would do, slowly enough to be watched ─────────

    private async Task AutoStartAsync()
    {
        Installer.Log("auto mode: the window is shown, the install starts by itself");
        await Task.Delay(2500);
        if (_accept != null) _accept.IsChecked = true;        // as if it was ticked
        await Task.Delay(1500);
        if (_install.IsEnabled) await RunInstallAsync();
        else { Installer.Log("FAILED: the Install button stayed disabled"); Finish(1); }
    }

    private async Task AutoDoneAsync()
    {
        await Task.Delay(3000);          // the "ASD is ready" page stays visible for a moment
        Installer.Log("auto mode: starting ASD");
        try { Installer.Launch(); } catch (Exception e) { Installer.Log("could not start ASD: " + e.Message); }
        await Task.Delay(500);
        Finish(0);
    }

    private async Task AutoErrorAsync()
    {
        await Task.Delay(5000);          // the error page stays visible for a moment
        Finish(1);
    }

    private static void Finish(int code)
    {
        Installer.Log($"exit code {code}");
        Environment.Exit(code);
    }

    // ───────── 2. progress ─────────

    private void ShowProgress()
    {
        var panel = Stack(14);
        var header = Header("Installing ASD", "Please wait");

        var steps = new StackPanel { Spacing = 10 };
        for (int i = 0; i < StepNames.Length; i++)
        {
            _stepMarks[i] = new TextBlock { Text = "•", Width = 22, FontSize = 16, Opacity = 0.5 };
            _stepTexts[i] = Text(StepNames[i], 15, dim: true);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_stepMarks[i]);
            row.Children.Add(_stepTexts[i]);
            steps.Children.Add(row);
        }
        panel.Children.Add(steps);

        _bar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 8, 0, 0) };
        _detail = Text("", 12, dim: true);
        panel.Children.Add(_bar);
        panel.Children.Add(_detail);

        Show(Page(header, panel), HeightSmall);
    }

    private enum Mark { Running, Done, Failed }

    private void SetStep(int index, Mark mark)
    {
        var (glyph, opacity, weight) = mark switch
        {
            Mark.Running => ("▶", 1.0, FontWeights.SemiBold),
            Mark.Done => ("✓", 0.9, FontWeights.Normal),
            _ => ("✕", 1.0, FontWeights.SemiBold),
        };
        _stepMarks[index].Text = glyph;
        _stepMarks[index].Opacity = opacity;
        _stepTexts[index].Opacity = mark == Mark.Done ? 0.75 : 1;
        _stepTexts[index].FontWeight = weight;
    }

    private void Report((double Fraction, string Text) p)
    {
        _bar.IsIndeterminate = p.Fraction < 0;
        if (p.Fraction >= 0) _bar.Value = Math.Clamp(p.Fraction * 100, 0, 100);
        _detail.Text = p.Text;
    }

    private void Note(string text)
    {
        _bar.IsIndeterminate = true;
        _detail.Text = text;
    }

    private async Task RunInstallAsync()
    {
        if (_working || _certificate == null) return;
        _working = true;
        Installer.Log("install started");
        ShowProgress();
        var ct = _cancel.Token;
        int step = 0;
        try
        {
            var progress = new Progress<(double Fraction, string Text)>(Report);

            // step 1: everything that comes before the install itself (Windows check, the package, the publisher's trust)
            SetStep(0, Mark.Running);
            Note("Checking Windows…");
            Installer.CheckWindows();

            Note("Getting ASD…");
            string package = _local ?? await Installer.DownloadAsync(progress, ct);
            if (_local != null) Note("Using the offline package.");

            Note("Windows may ask for your permission now.");
            await Installer.TrustAsync(_certificate, ct);
            SetStep(0, Mark.Done);

            // step 2: the install
            step = 1;
            SetStep(1, Mark.Running);
            Note("");
            await Installer.InstallAsync(package, progress);
            await Task.Run(Installer.CopyOfflineExtensions);   // an Extensions folder next to this installer, if there is one
            await Task.Run(Installer.RegisterFileType);        // .automation files open in ASD, with its icon
            SetStep(1, Mark.Done);

            Installer.Log("install finished");
            ShowDone();
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        catch (Exception e)
        {
            SetStep(step, Mark.Failed);
            Installer.Log($"FAILED at step {step + 1} ({StepNames[step]}): {e.Message}");
            ShowError(e is SetupException ? e.Message : "Something unexpected happened.\n\n" + e.Message);
        }
        finally
        {
            _working = false;
        }
    }

    // ───────── 3. done / 4. error ─────────

    private void ShowDone()
    {
        var installed = Installer.FindInstalled();
        var header = Header("ASD is ready", installed != null ? $"Version {Installer.VersionOf(installed)}" : "Installed");
        var text = Text("You can find ASD in the Start menu. It keeps itself up to date: when a new version is out, ASD offers it and Windows installs it in one step.", 14);

        var start = MakeButton("Start ASD", "StartButton", accent: true);
        start.Click += (_, _) =>
        {
            try { Installer.Launch(); } catch { }
            Close();
        };
        var close = MakeButton("Close", "CloseButton");
        close.Click += (_, _) => Close();

        Show(Page(header, text, close, start), HeightSmall);
        if (_auto) _ = AutoDoneAsync();
    }

    private void ShowError(string message, bool fatal = false)
    {
        var header = Header("ASD was not installed", "Nothing was changed");
        var text = new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        var close = MakeButton("Close", "CloseButton");
        close.Click += (_, _) => Close();
        Button[] buttons;
        if (fatal)
        {
            buttons = new[] { close };
        }
        else
        {
            var retry = MakeButton("Try again", "RetryButton", accent: true);
            retry.Click += async (_, _) => await RunInstallAsync();
            buttons = new[] { close, retry };
        }
        Show(Page(header, new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, buttons), HeightError);
        if (_auto) _ = AutoErrorAsync();
    }
}
