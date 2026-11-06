using Microsoft.UI.Xaml;

namespace ASD.Setup;

public partial class App : Application
{
    private SetupWindow? _window;   // keep a reference so the window isn't garbage-collected

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _window = new SetupWindow();
        _window.Activate();
    }
}
