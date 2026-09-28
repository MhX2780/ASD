using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed partial class SettingsPage : Page
{
    private bool _isLoading = true;

    public SettingsPage()
    {
        this.InitializeComponent();
        LoadCurrentSelection();
        _isLoading = false;
    }

    private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => CursorHelper.ApplyHandCursorToButtons(this);

    private void LoadCurrentSelection()
    {
        var mode = Windows.Storage.ApplicationData.Current.LocalSettings.Values["ThemeMode"] as string ?? "auto";
        ThemeRadioButtons.SelectedItem = mode switch
        {
            "light" => LightThemeOption,
            "dark" => DarkThemeOption,
            _ => AutoThemeOption,
        };
    }

    private void OnThemeChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_isLoading) return;

        var mode = ThemeRadioButtons.SelectedItem switch
        {
            RadioButton rb when rb == LightThemeOption => "light",
            RadioButton rb when rb == DarkThemeOption => "dark",
            _ => "auto",
        };

        Windows.Storage.ApplicationData.Current.LocalSettings.Values["ThemeMode"] = mode;
        (App.MainWindow as MainWindow)?.ApplyTheme(mode);
    }

    private void OnResetClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Windows.Storage.ApplicationData.Current.LocalSettings.Values["ThemeMode"] = "auto";
        (App.MainWindow as MainWindow)?.ApplyTheme("auto");
        _isLoading = true;
        ThemeRadioButtons.SelectedItem = AutoThemeOption;
        _isLoading = false;
    }
}
