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
        ClipToggle.IsOn = SettingsStore.Get("ClipboardDetect", "on") == "on";
        var mode = SettingsStore.Get("ThemeMode", "auto");
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

        SettingsStore.Set("ThemeMode", mode);
        (App.MainWindow as MainWindow)?.ApplyTheme(mode);
    }

    private void OnClipToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_isLoading) return;
        SettingsStore.Set("ClipboardDetect", ClipToggle.IsOn ? "on" : "off");
    }

    private void OnResetClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SettingsStore.Set("ThemeMode", "auto");
        (App.MainWindow as MainWindow)?.ApplyTheme("auto");
        SettingsStore.Set("ClipboardDetect", "on");
        _isLoading = true;
        ClipToggle.IsOn = true;
        ThemeRadioButtons.SelectedItem = AutoThemeOption;
        _isLoading = false;
    }
}
