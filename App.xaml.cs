using Microsoft.UI.Xaml;

namespace ASD;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Activate();
    }
}
