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
/// What it can do: Install, Update, Repair, Uninstall. Every view shows where you are (Welcome > the action > Done)
/// and every page that waits shows a progress bar.
/// </summary>
public sealed class SetupWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // window sizes in DIPs (outer size, title bar included): short, because each view only has a little to show
    private const int WindowWidth = 520;
    private const int HeightFirstInstall = 480;   // welcome page with the license
    private const int HeightSmall = 340;
    private const int HeightConfirm = 380;
    private const int HeightError = 440;
    private const int HeightExtensions = 560;

    private enum Mode { Install, Update, Repair, Uninstall }

    private readonly CancellationTokenSource _cancel = new();
    private readonly Grid _root = new() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
    private readonly Grid _frame = new() { Margin = new Thickness(32, 8, 32, 28) };
    private readonly string _license = Installer.ReadText("LICENSE");
    private readonly byte[]? _certificate = Installer.ReadCertificate();
    private readonly bool _auto;   // --auto: the window does by itself what a person would click (for watching a test)

    // welcome view
    private TextBlock _status = new();
    private ProgressBar _prepBar = new();
    private Button _install = new();    // Install (first time) or Update
    private Button? _repair;            // only when ASD is already installed
    private Button? _uninstall;         // only when ASD is already installed
    private Button? _closeButton;       // "Keep version" when a newer version is available
    private CheckBox? _accept;          // only on a first install
    private CheckBox? _deleteData;      // on the Uninstall page
    private Package? _installedPkg;
    private Mode _primaryMode = Mode.Install;

    // progress view
    private readonly FontIcon[] _stepMarks = new FontIcon[2];
    private readonly TextBlock[] _stepTexts = new TextBlock[2];
    private ProgressBar _bar = new();
    private TextBlock _detail = new();
    private TextBlock _percent = new();
    private int _step;

    private Mode _mode = Mode.Install;
    private bool _working;
    private bool _ready;
    private bool _placed;
    private string? _local;   // ASD.msix next to this installer
    private List<Installer.BundledExtension> _bundled = new();   // the extensions found in the Components folder
    private static bool _extSteps;                                // the Extensions step exists: Welcome > Install > Extensions > Done

    private static string[] StepNames(Mode mode) => mode switch
    {
        Mode.Uninstall => new[] { "Getting ready", "Removing ASD" },
        Mode.Update => new[] { "Getting ready to update", "Updating ASD" },
        Mode.Repair => new[] { "Getting ready to repair", "Repairing ASD" },
        _ => new[] { "Getting ready to install", "Installing ASD" },
    };

    private static string Verb(Mode mode) => mode switch
    {
        Mode.Uninstall => "Uninstall",
        Mode.Update => "Update",
        Mode.Repair => "Repair",
        _ => "Install",
    };

    public SetupWindow(bool auto = false)
    {
        _auto = auto;
        Title = "ASD Setup";
        Loc.Init();   // the saved language, or the Windows language when there is a translation of it
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* an older Windows: plain background */ }

        var titleBar = new TitleBar
        {
            Title = "ASD Setup",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
        };
        // (the language box is on the Welcome page: the title bar is too narrow for it at the default window size)
        var titleLogo = LoadLogo();
        if (titleLogo != null) titleBar.IconSource = new ImageIconSource { ImageSource = titleLogo };   // the ASD logo left of the title
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(_frame, 1);
        _root.Children.Add(titleBar);
        _root.Children.Add(_frame);
        Content = _root;
        Loc.Track(_root, _frame);   // translates every view (and mirrors it for Arabic) after it is built

        SetupFrame(titleBar);
        Closed += (_, _) => _cancel.Cancel();

        ShowLoading();
        _ = PrepareAsync();
    }

    /// <summary>A small row with a globe and the language box, at the bottom of the Welcome page.</summary>
    private FrameworkElement LanguageRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(MakeLanguagePicker());
        return row;
    }

    /// <summary>"Auto" (the Windows language) or a language. The choice is saved in ASD's settings, so ASD opens in the same language.</summary>
    private ComboBox MakeLanguagePicker()
    {
        var combo = new ComboBox { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(combo, "LanguageBox");
        ToolTipService.SetToolTip(combo, "Language");
        combo.Items.Add(new ComboBoxItem { Content = Loc.T("Auto"), Tag = "auto" });
        foreach (var l in Loc.Available) combo.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        combo.SelectedIndex = 0;
        var saved = Loc.Setting;
        for (var i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem { Tag: string tag } && tag == saved) combo.SelectedIndex = i;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string code } && code != Loc.Setting) Loc.Set(code);
        };
        return combo;
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
    /// One view: the header (path + logo + title) on top, the content under it, and the buttons pinned to the bottom right.
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
            // Close / Cancel on the left, every other button on the right
            var bar = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 12, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in buttons)
            {
                var onLeft = b.Content is string label && (label == "Close" || label == "Cancel");
                (onLeft ? left : right).Children.Add(b);
            }
            Grid.SetColumn(right, 1);
            bar.Children.Add(left);
            bar.Children.Add(right);
            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);
        }
        return grid;
    }

    private static StackPanel Stack(double spacing) => new() { Spacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static Button MakeButton(string label, string id, bool accent = false)
    {
        var button = new Button { Content = label, MinWidth = 96, Padding = new Thickness(14, 7, 14, 7) };
        AutomationProperties.SetAutomationId(button, id);   // so a test can find the button by name, not by position
        if (accent) SetAccent(button, true);
        return button;
    }

    private static void SetAccent(Button button, bool accent)
    {
        try
        {
            if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            else button.ClearValue(FrameworkElement.StyleProperty);
        }
        catch { }
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

    /// <summary>Where you are: Welcome > Install (or Update, Repair, Uninstall) > Done. The step you are on is bold, finished ones have a check mark.</summary>
    private static FrameworkElement Nav(int current, string middle)
    {
        var labels = _extSteps && middle != "Uninstall"
            ? new[] { "Welcome", middle, "Extensions", "Done" }
            : new[] { "Welcome", middle, "Done" };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 16) };
        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) row.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            bool passed = i < current;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            if (passed) item.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Opacity = 0.8 });
            item.Children.Add(new TextBlock
            {
                Text = labels[i],
                FontSize = 12,
                FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal,
                Opacity = i == current ? 1 : passed ? 0.8 : 0.5,
            });
            row.Children.Add(item);
        }
        return row;
    }

    private static FrameworkElement Header(string title, string subtitle, FrameworkElement? nav = null)
    {
        FrameworkElement logo;
        var bitmap = LoadLogo();
        logo = bitmap != null
            ? new Image { Source = bitmap, Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = "", FontSize = 40 };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Text(title, 26, bold: true));
        texts.Children.Add(Text(subtitle, 13, dim: true));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(logo);
        row.Children.Add(texts);

        var all = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        if (nav != null) all.Children.Add(nav);
        all.Children.Add(row);
        return all;
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
        var panel = Stack(10);
        panel.Children.Add(Text("Looking at what is installed…", 13, dim: true));
        panel.Children.Add(new ProgressBar { IsIndeterminate = true });
        Show(Page(Header("ASD Setup", "Getting ready", Nav(0, "Install")), panel), HeightSmall);
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

        _ready = false;
        var extensionsTask = Installer.FindExtensionsAsync();   // Components compared with the server, at the same time
        var installed = await Task.Run(Installer.FindInstalled);
        _local = Installer.LocalPackage();
        _bundled = await extensionsTask;
        _extSteps = _bundled.Count > 0;
        ShowWelcome(installed);

        string line;
        Version? latest = null;
        if (_local != null)
            line = Installer.IsInComponents(_local)
                ? "Using ASD.msix from the Components folder (no download needed)."
                : "Using the ASD.msix next to this installer (no download needed).";
        else
        {
            latest = await Installer.TryLatestVersionAsync();
            line = latest != null
                ? $"Latest version: {latest}"
                : "No internet connection right now. Check it, then press the button again.";
        }

        if (installed != null)
        {
            var have = Installer.VersionOf(installed);
            bool upToDate = latest != null && have >= latest;
            line = upToDate
                ? $"ASD {have} is installed and up to date."
                : $"ASD {have} is installed.";
            if (latest != null && have < latest)
            {
                // the person chooses: Update downloads the newest version, "Keep version" leaves ASD as it is
                line += $" Version {latest} is available: press Update to get it, or keep this version.";
                if (_closeButton != null) _closeButton.Content = "Keep version";
            }

            if (upToDate)
            {
                // nothing to update: the main action is Repair
                _install.Visibility = Visibility.Collapsed;
                if (_repair != null) SetAccent(_repair, true);
                _primaryMode = Mode.Repair;
            }
            else _primaryMode = Mode.Update;
        }

        _status.Text = line;
        _prepBar.Visibility = Visibility.Collapsed;
        _ready = true;
        UpdateInstallEnabled();
        if (_auto) _ = AutoStartAsync();
    }

    // ───────── 1. welcome (the license only on a first install) ─────────

    private void ShowWelcome(Package? installed)
    {
        bool firstInstall = installed == null;
        _installedPkg = installed;
        _primaryMode = firstInstall ? Mode.Install : Mode.Update;

        var panel = Stack(12);
        var header = Header("ASD Setup", firstInstall ? "Installs ASD on this PC" : "ASD is already installed",
            Nav(0, firstInstall ? "Install" : "Update"));

        _status = Text("Looking up the latest version…", 13, dim: true);
        _prepBar = new ProgressBar { IsIndeterminate = true };
        panel.Children.Add(_status);
        panel.Children.Add(_prepBar);

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

        panel.Children.Add(LanguageRow());

        _install = MakeButton(firstInstall ? "Install" : "Update", "InstallButton", accent: true);
        _install.IsEnabled = false;
        _install.Click += async (_, _) => await RunAsync(firstInstall ? Mode.Install : Mode.Update);

        var close = MakeButton("Close", "CloseButton");
        close.Click += (_, _) => Close();
        _closeButton = close;

        var buttons = new List<Button> { close };
        _repair = null;
        _uninstall = null;
        if (!firstInstall)
        {
            _uninstall = MakeButton("Uninstall", "UninstallButton");
            _uninstall.IsEnabled = false;
            _uninstall.Click += (_, _) => ShowConfirmUninstall();
            _repair = MakeButton("Repair", "RepairButton");
            _repair.IsEnabled = false;
            _repair.Click += async (_, _) => await RunAsync(Mode.Repair);
            buttons.Add(_uninstall);
            buttons.Add(_repair);
        }
        buttons.Add(_install);

        Show(Page(header, panel, buttons.ToArray()), (firstInstall ? HeightFirstInstall : HeightSmall) + 44);   // + the language row
    }

    private void UpdateInstallEnabled()
    {
        bool free = _ready && !_working;
        _install.IsEnabled = free && (_accept == null || _accept.IsChecked == true);
        if (_repair != null) _repair.IsEnabled = free;
        if (_uninstall != null) _uninstall.IsEnabled = free;
    }

    // ───────── --auto: do what a person would do, slowly enough to be watched ─────────

    private async Task AutoStartAsync()
    {
        Installer.Log("auto mode: the window is shown, the install starts by itself");
        await Task.Delay(2500);
        if (_accept != null) _accept.IsChecked = true;        // as if it was ticked
        await Task.Delay(1500);
        var primary = _install.Visibility == Visibility.Visible ? _install : _repair;
        if (primary != null && primary.IsEnabled) await RunAsync(_primaryMode);
        else { Installer.Log("FAILED: the main button stayed disabled"); Finish(1); }
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

    // ───────── 1b. are you sure (Uninstall) ─────────

    private void ShowConfirmUninstall()
    {
        var header = Header("Remove ASD?", "It will be removed from this PC", Nav(0, "Uninstall"));
        var panel = Stack(12);
        panel.Children.Add(Text("ASD is removed from Windows. Your settings, keys and extensions are kept unless you tick the box below, so a later install finds them again.", 14));
        _deleteData = new CheckBox { Content = "Also delete my settings, keys and extensions" };
        AutomationProperties.SetAutomationId(_deleteData, "DeleteData");
        panel.Children.Add(_deleteData);

        var cancel = MakeButton("Cancel", "CancelButton");
        cancel.Click += (_, _) => { ShowLoading(); _ = PrepareAsync(); };
        var remove = MakeButton("Uninstall", "ConfirmUninstallButton", accent: true);
        remove.Click += async (_, _) => await RunAsync(Mode.Uninstall);
        Show(Page(header, panel, cancel, remove), HeightConfirm);
    }

    // ───────── 2. progress ─────────

    private void ShowProgress()
    {
        var names = StepNames(_mode);
        var panel = Stack(14);
        var header = Header(_mode switch
        {
            Mode.Uninstall => "Removing ASD",
            Mode.Update => "Updating ASD",
            Mode.Repair => "Repairing ASD",
            _ => "Installing ASD",
        }, "Please wait", Nav(1, Verb(_mode)));

        var steps = new StackPanel { Spacing = 10 };
        for (int i = 0; i < names.Length; i++)
        {
            _stepMarks[i] = new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.5, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            _stepTexts[i] = Text(names[i], 15, dim: true);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_stepMarks[i]);
            row.Children.Add(_stepTexts[i]);
            steps.Children.Add(row);
        }
        panel.Children.Add(steps);

        _bar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 0) };
        _detail = Text("", 12, dim: true);
        _percent = Text("", 12, dim: true);
        panel.Children.Add(_bar);
        panel.Children.Add(_detail);
        panel.Children.Add(_percent);

        Show(Page(header, panel), HeightSmall);
    }

    private enum Mark { Running, Done, Failed }

    /// <summary>Fluent icons (Segoe Fluent Icons): a ring = waiting, play = running, check mark = done, cross = failed.</summary>
    private void SetStep(int index, Mark mark)
    {
        var (glyph, opacity, weight) = mark switch
        {
            Mark.Running => ("", 1.0, FontWeights.SemiBold),
            Mark.Done => ("", 0.9, FontWeights.Normal),
            _ => ("", 1.0, FontWeights.SemiBold),
        };
        if (mark == Mark.Running) _step = index;
        _stepMarks[index].Glyph = glyph;
        _stepMarks[index].Opacity = opacity;
        _stepTexts[index].Opacity = mark == Mark.Done ? 0.75 : 1;
        _stepTexts[index].FontWeight = weight;
    }

    /// <summary>One bar for the whole job: the first step fills the first half, the second step the second half.</summary>
    private void Report((double Fraction, string Text) p)
    {
        if (p.Fraction < 0)
        {
            _bar.IsIndeterminate = true;   // no known progress (for example while the connection is lost)
            _percent.Text = "";
        }
        else
        {
            var overall = (_step + Math.Clamp(p.Fraction, 0, 1)) / 2.0 * 100;
            _bar.IsIndeterminate = false;
            _bar.Value = overall;
            _percent.Text = $"{overall:0}%";
        }
        _detail.Text = p.Text;
    }

    private void Note(string text)
    {
        _detail.Text = text;
    }

    private async Task RunAsync(Mode mode)
    {
        if (_working || _certificate == null) return;
        _working = true;
        _mode = mode;
        bool deleteData = _deleteData?.IsChecked == true;
        Installer.Log($"{Verb(mode).ToLowerInvariant()} started");
        ShowProgress();
        var names = StepNames(mode);
        var ct = _cancel.Token;
        int step = 0;
        try
        {
            var progress = new Progress<(double Fraction, string Text)>(Report);

            // step 1: everything that comes before the real work (Windows check, the package, the publisher's trust)
            SetStep(0, Mark.Running);
            Report((0.1, "Checking Windows…"));
            Installer.CheckWindows();

            if (mode == Mode.Uninstall)
            {
                SetStep(0, Mark.Done);
                step = 1;
                SetStep(1, Mark.Running);
                Report((0, "Windows is removing ASD…"));
                await Installer.UninstallAsync(deleteData, progress);
                SetStep(1, Mark.Done);
                Installer.Log("uninstall finished");
                ShowDone(deleteData);
                return;
            }

            string package;
            if (_local != null)
            {
                Report((0.5, "Using ASD.msix from the Components folder (no download needed)."));
                package = _local;
            }
            else
            {
                Report((0.2, "Getting ASD…"));
                package = await Installer.DownloadAsync(progress, ct);
            }

            Report((0.95, "Windows may ask for your permission now."));
            await Installer.TrustAsync(_certificate, ct);
            Report((1, "Ready."));
            SetStep(0, Mark.Done);

            // step 2: the install
            step = 1;
            SetStep(1, Mark.Running);
            Report((0, "Windows is installing ASD…"));
            await Installer.InstallAsync(package, progress);
            Report((1, "Finishing…"));
            await Task.Run(Installer.CopyOfflineExtensions);   // an Extensions folder next to this installer, if there is one
            await Task.Run(Installer.RegisterFileType);        // .automation files open in ASD, with its icon
            SetStep(1, Mark.Done);

            Installer.Log("install finished");
            if (_extSteps) ShowExtensions();   // Welcome > Install > Extensions > Done
            else ShowDone(false);
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        catch (Exception e)
        {
            SetStep(step, Mark.Failed);
            Installer.Log($"FAILED at step {step + 1} ({names[step]}): {e.Message}");
            ShowError(e is SetupException ? e.Message : "Something went wrong.\n\n" + e.Message);
        }
        finally
        {
            _working = false;
        }
    }

    // ───────── 3. done / 4. error ─────────

    // ───────── 2b. extensions: choose what to install from the Components folder, or skip ─────────

    private void ShowExtensions()
    {
        var header = Header("Extensions", "Add extra tools to ASD", Nav(2, Verb(_mode)));

        var list = Stack(8);
        var boxes = new List<(CheckBox Box, Installer.BundledExtension Item)>();
        foreach (var item in _bundled)
        {
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(Text(item.Version.Length > 0 ? $"{item.Name}  ·  {item.Version}" : item.Name, 14, bold: true));
            if (item.Description.Length > 0) text.Children.Add(Text(item.Description, 12, dim: true));
            if (item.Note.Length > 0) text.Children.Add(Text(item.Note, 12, dim: true));
            // what is in Components (or newer than it) is ticked; an extension that exists only on the server is the person's choice
            var box = new CheckBox { Content = text, IsChecked = !(item.Remote && item.Note == Installer.ServerOnlyNote), MinWidth = 0 };
            AutomationProperties.SetAutomationId(box, "Extension_" + item.Id);
            boxes.Add((box, item));
            list.Children.Add(box);
        }

        var all = new HyperlinkButton { Content = "Select all", Padding = new Thickness(0) };
        var none = new HyperlinkButton { Content = "Select none", Padding = new Thickness(0) };
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        links.Children.Add(all);
        links.Children.Add(none);

        var panel = Stack(10);
        panel.Children.Add(Text("Choose the extensions to install now. You can add them later from Extensions inside ASD.", 13, dim: true));
        panel.Children.Add(links);
        panel.Children.Add(new ScrollViewer { Content = list, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) });

        var skip = MakeButton("Skip", "SkipButton");
        var install = MakeButton("Install", "InstallExtensionsButton", accent: true);
        void Refresh() => install.IsEnabled = boxes.Any(b => b.Box.IsChecked == true);
        foreach (var (box, _) in boxes)
        {
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
        }
        Refresh();
        all.Click += (_, _) => { foreach (var (box, _) in boxes) box.IsChecked = true; };
        none.Click += (_, _) => { foreach (var (box, _) in boxes) box.IsChecked = false; };
        skip.Click += (_, _) => { Installer.Log("the extensions step was skipped"); ShowDone(false); };
        install.Click += async (_, _) => await InstallExtensionsAsync(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Item).ToList());

        Show(Page(header, panel, skip, install), HeightExtensions);
        if (_auto) _ = AutoExtensionsAsync();
    }

    private async Task AutoExtensionsAsync()
    {
        await Task.Delay(2500);
        await InstallExtensionsAsync(_bundled.ToList());
    }

    private async Task InstallExtensionsAsync(List<Installer.BundledExtension> items)
    {
        if (_working || items.Count == 0) return;
        _working = true;
        try
        {
            var header = Header("Installing Extensions", "Please wait", Nav(2, Verb(_mode)));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Text("Installing Extensions", 15, bold: true));
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(0, 8, 0, 0) };
            var detail = Text("", 12, dim: true);
            var percent = Text("0%", 12, dim: true);
            var panel = Stack(14);
            panel.Children.Add(row);
            panel.Children.Add(bar);
            panel.Children.Add(detail);
            panel.Children.Add(percent);
            Show(Page(header, panel), HeightSmall);

            var progress = new Progress<(double Fraction, string Text)>(p =>
            {
                bar.IsIndeterminate = p.Fraction < 0;
                bar.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
                percent.Text = p.Fraction < 0 ? "" : $"{bar.Value:0}%";
                detail.Text = p.Text;
            });
            Installer.Log($"installing {items.Count} extension(s) from the Components folder");
            await Installer.InstallChosenAsync(items, progress, _cancel.Token);
            await Task.Delay(400);   // so the full bar is seen
        }
        catch (Exception e)
        {
            Installer.Log("FAILED installing the extensions: " + e.Message);
        }
        finally
        {
            _working = false;
        }
        ShowDone(false);
    }

    private void ShowDone(bool dataDeleted)
    {
        var nav = Nav(_extSteps && _mode != Mode.Uninstall ? 3 : 2, Verb(_mode));
        var close = MakeButton("Close", "CloseButton");
        close.Click += (_, _) => Close();

        if (_mode == Mode.Uninstall)
        {
            var header = Header("ASD was removed", "Thank you for using it", nav);
            var text = Text(dataDeleted
                ? "ASD and its data were removed from this PC."
                : "ASD was removed from this PC. Your settings, keys and extensions were kept: install ASD again and they are back.", 14);
            Show(Page(header, text, close), HeightSmall);
            if (_auto) _ = AutoErrorAsync();
            return;
        }

        var installed = Installer.FindInstalled();
        var title = _mode switch { Mode.Update => "ASD was updated", Mode.Repair => "ASD was repaired", _ => "ASD is ready" };
        var head = Header(title, installed != null ? $"Version {Installer.VersionOf(installed)}" : "Installed", nav);
        var body = Text("You can find ASD in the Start menu. It keeps itself up to date: when a new version is out, ASD offers it and Windows installs it in one step."
            + (Installer.ExtensionsInstalled > 0 ? $"\n\n{Installer.ExtensionsInstalled} extension(s) were installed." : "")
            + (Installer.ExtensionsFailed > 0 ? $"\n\n{Installer.ExtensionsFailed} extension(s) could not be installed (see the log)." : ""), 14);

        var start = MakeButton("Start ASD", "StartButton", accent: true);
        start.Click += (_, _) =>
        {
            try { Installer.Launch(); } catch { }
            Close();
        };

        Show(Page(head, body, close, start), HeightSmall);
        if (_auto) _ = AutoDoneAsync();
    }

    /// <summary>The text of an error page without web addresses or the name of the download site.</summary>
    private static string Clean(string text)
    {
        var noLinks = System.Text.RegularExpressions.Regex.Replace(text, @"https?://\S+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return noLinks.Replace("GitHub", "the server", StringComparison.OrdinalIgnoreCase).Trim();
    }

    private void ShowError(string message, bool fatal = false)
    {
        var title = _mode switch
        {
            Mode.Uninstall => "ASD was not removed",
            Mode.Update => "ASD was not updated",
            Mode.Repair => "ASD was not repaired",
            _ => "ASD was not installed",
        };
        var header = Header("Something went wrong", title, Nav(1, Verb(_mode)));
        var text = new TextBlock { Text = Clean(message), FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

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
            retry.Click += async (_, _) => await RunAsync(_mode);
            buttons = new[] { close, retry };
        }
        Show(Page(header, new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, buttons), HeightError);
        if (_auto) _ = AutoErrorAsync();
    }
}
