using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace ASD.Setup;

/// <summary>
/// The whole installer: one window with four views (welcome + license, progress, done, error).
/// Built in code, like the other windows in ASD.
/// </summary>
public sealed class SetupWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly CancellationTokenSource _cancel = new();
    private readonly Grid _frame = new() { Padding = new Thickness(36, 28, 36, 28) };
    private readonly string _license = Installer.ReadText("LICENSE");
    private readonly byte[]? _certificate = Installer.ReadCertificate();

    // welcome view
    private TextBlock _status = new();
    private Button _install = new();
    private CheckBox _accept = new();

    // progress view
    private readonly TextBlock[] _stepMarks = new TextBlock[4];
    private readonly TextBlock[] _stepTexts = new TextBlock[4];
    private ProgressBar _bar = new();
    private TextBlock _detail = new();

    private bool _working;
    private string? _local;   // ASD.msix next to this installer

    private static readonly string[] StepNames =
    {
        "Checking Windows",
        "Getting ASD",
        "Trusting the publisher (asks for permission once)",
        "Installing ASD",
    };

    public SetupWindow()
    {
        Title = "ASD Setup";
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* an older Windows: plain background */ }
        Content = _frame;
        Closed += (_, _) => _cancel.Cancel();

        PlaceWindow();
        ShowWelcome();
    }

    // ───────── window ─────────

    private void PlaceWindow()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch { }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
            int w = (int)(640 * scale), h = (int)(720 * scale);
            AppWindow.Resize(new SizeInt32(w, h));

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }
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

    private static StackPanel Stack(double spacing)
        => new() { Spacing = spacing, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center };

    private static Button MakeButton(string label, bool accent = false)
    {
        var button = new Button { Content = label, MinWidth = 120, Padding = new Thickness(18, 8, 18, 8) };
        if (accent)
        {
            try { button.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; } catch { }
        }
        return button;
    }

    private static FrameworkElement Header(string title, string subtitle)
    {
        FrameworkElement logo;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            logo = File.Exists(path)
                ? new Image { Source = new BitmapImage(new Uri(path)), Width = 56, Height = 56 }
                : new FontIcon { Glyph = "", FontSize = 40 };
        }
        catch { logo = new FontIcon { Glyph = "", FontSize = 40 }; }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Text(title, 26, bold: true));
        texts.Children.Add(Text(subtitle, 13, dim: true));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(logo);
        row.Children.Add(texts);
        return row;
    }

    private void Show(FrameworkElement view)
    {
        _frame.Children.Clear();
        _frame.Children.Add(view);
    }

    // ───────── 1. welcome and license ─────────

    private void ShowWelcome()
    {
        var panel = Stack(14);
        panel.Children.Add(Header("ASD Setup", "Installs ASD on this PC"));

        _status = Text("Looking at what is installed…", 13, dim: true);
        panel.Children.Add(_status);

        panel.Children.Add(Text("License", 14, bold: true));
        var licenseBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
            Child = new ScrollViewer
            {
                Height = 230,
                Padding = new Thickness(14, 10, 14, 10),
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(_license) ? "No license text is included." : _license,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
        };
        panel.Children.Add(licenseBox);

        _accept = new CheckBox { Content = "I accept the license" };
        panel.Children.Add(_accept);

        panel.Children.Add(Text(
            "ASD is installed for your account. Windows asks for administrator permission once, only to trust ASD's own certificate. " +
            "Later updates are installed from inside ASD.", 12, dim: true));

        _install = MakeButton("Install", accent: true);
        _install.IsEnabled = false;
        _install.Click += async (_, _) => await RunInstallAsync();
        _accept.Checked += (_, _) => UpdateInstallEnabled();
        _accept.Unchecked += (_, _) => UpdateInstallEnabled();

        var close = MakeButton("Close");
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(close);
        buttons.Children.Add(_install);
        panel.Children.Add(buttons);

        Show(panel);
        _ = PrepareAsync();
    }

    private bool _ready;

    private void UpdateInstallEnabled() => _install.IsEnabled = _ready && _accept.IsChecked == true && !_working;

    /// <summary>Finds out what is installed, whether an offline package is here, and what GitHub has.</summary>
    private async Task PrepareAsync()
    {
        if (_certificate == null)
        {
            _status.Text = "This installer was built without ASD's certificate, so it cannot install ASD. Download the Setup from the ASD release page.";
            return;
        }

        var installed = await Task.Run(Installer.FindInstalled);
        _local = Installer.LocalPackage();

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
            line = (latest != null && have >= latest)
                ? $"ASD {have} is installed and up to date. Install again to repair it."
                : $"ASD {have} is installed. This will update it.";
            if (latest != null && have < latest) line += $" (Latest: {latest})";
            _install.Content = latest != null && have >= latest ? "Repair" : "Update";
        }

        _status.Text = line;
        _ready = true;
        UpdateInstallEnabled();
    }

    // ───────── 2. progress ─────────

    private void ShowProgress()
    {
        var panel = Stack(18);
        panel.Children.Add(Header("Installing ASD", "Please wait"));

        var steps = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
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

        _bar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 16, 0, 0) };
        _detail = Text("", 12, dim: true);
        panel.Children.Add(_bar);
        panel.Children.Add(_detail);

        Show(panel);
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

    private async Task RunInstallAsync()
    {
        if (_working || _certificate == null) return;
        _working = true;
        ShowProgress();
        var ct = _cancel.Token;
        int step = 0;
        try
        {
            var progress = new Progress<(double Fraction, string Text)>(Report);

            SetStep(0, Mark.Running);
            Installer.CheckWindows();
            SetStep(0, Mark.Done);

            step = 1;
            SetStep(1, Mark.Running);
            string package = _local ?? await Installer.DownloadAsync(progress, ct);
            if (_local != null) StepNote("Using the offline package.");
            SetStep(1, Mark.Done);

            step = 2;
            SetStep(2, Mark.Running);
            StepNote("Windows may ask for permission now.");
            await Installer.TrustAsync(_certificate, ct);
            SetStep(2, Mark.Done);

            step = 3;
            SetStep(3, Mark.Running);
            StepNote("");
            await Installer.InstallAsync(package, progress);
            SetStep(3, Mark.Done);

            ShowDone();
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        catch (Exception e)
        {
            SetStep(step, Mark.Failed);
            ShowError(e is SetupException ? e.Message : "Something unexpected happened.\n\n" + e.Message);
        }
        finally
        {
            _working = false;
        }
    }

    private void StepNote(string text)
    {
        _bar.IsIndeterminate = true;
        _detail.Text = text;
    }

    // ───────── 3. done / 4. error ─────────

    private void ShowDone()
    {
        var installed = Installer.FindInstalled();
        var panel = Stack(16);
        panel.Children.Add(Header("ASD is ready", installed != null ? $"Version {Installer.VersionOf(installed)}" : "Installed"));
        panel.Children.Add(Text("You can find ASD in the Start menu. It keeps itself up to date: when a new version is out, ASD offers it and Windows installs it in one step.", 14));

        var start = MakeButton("Start ASD", accent: true);
        start.Click += (_, _) =>
        {
            try { Installer.Launch(); } catch { }
            Close();
        };
        var close = MakeButton("Close");
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(close);
        buttons.Children.Add(start);
        panel.Children.Add(buttons);
        Show(panel);
    }

    private void ShowError(string message)
    {
        var panel = Stack(16);
        panel.Children.Add(Header("ASD was not installed", "Nothing was changed"));
        panel.Children.Add(new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var retry = MakeButton("Try again", accent: true);
        retry.Click += async (_, _) => await RunInstallAsync();
        var close = MakeButton("Close");
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(close);
        buttons.Children.Add(retry);
        panel.Children.Add(buttons);
        Show(panel);
    }
}
