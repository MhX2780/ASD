using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ASD;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    private static Window? _installWindow;   // keep a reference so the window isn't garbage-collected

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        // ASD.exe --run / --check file.automation: run without a window and exit (see AutomationCli).
        var cli = Environment.GetCommandLineArgs();
        if (AutomationCli.Wants(cli))
        {
            var code = Task.Run(() => AutomationCli.RunAsync(cli)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        // The installed ASD runs once. A second start (a Jump List item, a double-clicked .automation file) is handed to the
        // ASD that is already open, and this process quits at once.
        if (AppIdentity.IsPackaged)
        {
            var main = AppInstance.FindOrRegisterForKey("ASD-main");
            if (!main.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                _ = Task.Run(async () =>
                {
                    try { await main.RedirectActivationToAsync(activation); } catch { }
                    Environment.Exit(0);
                });
                return;
            }
            main.Activated += (_, a) => LaunchRouter.Deliver(a);
        }

        // cd-r.txt next to ASD.exe = running from a read-only disc (CD-R / DVD):
        // copy the app to a folder the user picks first, instead of opening the main window.
        if (InstallWindow.IsDiscCopy)
        {
            _installWindow = new InstallWindow();
            _installWindow.Activate();
            return;
        }

        ExtensionLoader.LoadAll();      // installed extensions add their pages before the window is built

        var window = new MainWindow();
        MainWindow = window;
        window.Activate();

        if (AppIdentity.IsPackaged)
        {
            // started from a Jump List item, or by opening an .automation file
            window.HandleLaunch(LaunchRouter.Read(AppInstance.GetCurrent().GetActivatedEventArgs()));
            _ = AppJumpList.SetupAsync();
        }
    }
}
