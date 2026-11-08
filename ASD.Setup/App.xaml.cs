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
        // whatever makes the installer close by itself is written to the log (%TEMP%\ASD-Setup\setup.log), so it can be read afterwards
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Installer.Log("CRASH (unhandled): " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Installer.Log("unobserved task error: " + e.Exception);
        UnhandledException += (_, e) => Installer.Log("CRASH (xaml): " + e.Exception);
        Installer.Log("ASD-Setup started: " + string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));
        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            // an installer that was renamed can not find its resources (they are looked up by the exe's name): run again under the right name
            Installer.Log("InitializeComponent failed: " + ex);
            if (RelaunchUnderOwnName()) Environment.Exit(0);
            throw;
        }
    }

    /// <summary>
    /// When ASD-Setup.exe was renamed, WinUI cannot find its resources and the window never opens. This copies the exe to a temp folder
    /// under its real name and starts that copy (with --from = the folder it came from, so an ASD.msix next to the original is still used).
    /// Returns false when this exe already has the right name or the copy could not be started.
    /// </summary>
    private static bool RelaunchUnderOwnName()
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self) || Path.GetFileName(self).Equals("ASD-Setup.exe", StringComparison.OrdinalIgnoreCase)) return false;

            var folder = Path.Combine(Path.GetTempPath(), "ASD-Setup", "run", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, "ASD-Setup.exe");
            File.Copy(self, copy, overwrite: true);

            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            if (!args.Any(a => a.Equals("--from", StringComparison.OrdinalIgnoreCase)))
            {
                args.Add("--from");
                args.Add(Path.GetDirectoryName(self)!);
            }
            var start = new System.Diagnostics.ProcessStartInfo(copy) { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(start);
            Installer.Log("renamed installer: started a copy under its own name: " + copy);
            return true;
        }
        catch (Exception e)
        {
            Installer.Log("could not start a copy under the right name: " + e.Message);
            return false;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        var at = args.FindIndex(a => a.Equals("--log", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < args.Count) Installer.LogFile = args[at + 1];

        var from = args.FindIndex(a => a.Equals("--from", StringComparison.OrdinalIgnoreCase));
        if (from >= 0 && from + 1 < args.Count) Installer.SourceFolder = args[from + 1];

        if (Has("--silent"))
        {
            _ = Silent.RunAsync(start: Has("--start"));
            return;
        }

        try
        {
            _window = new SetupWindow(auto: Has("--auto"));
            _window.Activate();
        }
        catch (Exception ex)
        {
            Installer.Log("the window could not be created: " + ex);
            if (RelaunchUnderOwnName()) { Environment.Exit(0); return; }
            throw;
        }
    }
}
