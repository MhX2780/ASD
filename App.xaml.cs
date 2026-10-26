using Microsoft.UI.Xaml;

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
        // cd-r.txt next to ASD.exe = running from a read-only disc (CD-R / DVD):
        // copy the app to a folder the user picks first, instead of opening the main window.
        if (InstallWindow.IsDiscCopy)
        {
            _installWindow = new InstallWindow();
            _installWindow.Activate();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Activate();
    }
}
