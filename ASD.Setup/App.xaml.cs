using Microsoft.UI.Xaml;

namespace ASD.Setup;

/// <summary>
/// ASD-Setup.exe            the installer window
/// ASD-Setup.exe --auto     the same window, but it accepts the license and installs by itself, then starts ASD (to WATCH a test)
/// ASD-Setup.exe --silent   no window at all (add --start to start ASD afterwards)
/// --log FILE               where to write the log (default: %TEMP%\ASD-Setup\setup.log)
/// </summary>
public partial class App : Application
{
    private SetupWindow? _window;   // keep a reference so the window isn't garbage-collected

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        var at = args.FindIndex(a => a.Equals("--log", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Count) Installer.LogFile = args[at + 1];

        if (Has("--silent"))
        {
            _ = Silent.RunAsync(start: Has("--start"));
            return;
        }

        _window = new SetupWindow(auto: Has("--auto"));
        _window.Activate();
    }
}
