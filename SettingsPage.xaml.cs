using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed partial class SettingsPage : Page
{
    private bool _isLoading = true;
    private static readonly int[] SlowSeconds = { 1, 3, 5, 10, 30 };
    private static readonly int[] ExpiryDays = { 3, 7, 14, 30, 60 };

    public SettingsPage()
    {
        this.InitializeComponent();
        foreach (var s in SlowSeconds) SlowCombo.Items.Add(s == 1 ? "1 second" : $"{s} seconds");
        foreach (var d in ExpiryDays) ExpiryCombo.Items.Add($"{d} days");
        LoadCurrentSelection();
        _isLoading = false;
    }

    private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => CursorHelper.ApplyHandCursorToButtons(this);

    private void LoadCurrentSelection()
    {
        ClipToggle.IsOn = SettingsStore.Get("ClipboardDetect", "on") == "on";
        NotifyToggle.IsOn = SettingsStore.Get("notify.enabled", "on") == "on";
        ToastToggle.IsOn = SettingsStore.Get("notify.windows", "on") == "on";
        HideNamesToggle.IsOn = SettingsStore.Get("notify.hideNames", "off") == "on";
        SlowCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SlowSeconds, int.TryParse(SettingsStore.Get("notify.slowSec", "3"), out var ss) ? ss : 3));
        ExpiryCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ExpiryDays, int.TryParse(SettingsStore.Get("keys.expiryDays", "7"), out var ed) ? ed : 7));
        ToastToggle.IsEnabled = HideNamesToggle.IsEnabled = NotifyToggle.IsOn;
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

    private void OnNotifyChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_isLoading) return;
        SettingsStore.Set("notify.enabled", NotifyToggle.IsOn ? "on" : "off");
        SettingsStore.Set("notify.windows", ToastToggle.IsOn ? "on" : "off");
        SettingsStore.Set("notify.hideNames", HideNamesToggle.IsOn ? "on" : "off");
        if (SlowCombo.SelectedIndex >= 0) SettingsStore.Set("notify.slowSec", SlowSeconds[SlowCombo.SelectedIndex].ToString());
        if (ExpiryCombo.SelectedIndex >= 0) SettingsStore.Set("keys.expiryDays", ExpiryDays[ExpiryCombo.SelectedIndex].ToString());
        ToastToggle.IsEnabled = HideNamesToggle.IsEnabled = NotifyToggle.IsOn;
    }

    private void OnResetClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        SettingsStore.Set("ThemeMode", "auto");
        (App.MainWindow as MainWindow)?.ApplyTheme("auto");
        SettingsStore.Set("ClipboardDetect", "on");
        SettingsStore.SetMany(new[]
        {
            new KeyValuePair<string, string>("notify.enabled", "on"), new KeyValuePair<string, string>("notify.windows", "on"),
            new KeyValuePair<string, string>("notify.hideNames", "off"), new KeyValuePair<string, string>("notify.slowSec", "3"),
            new KeyValuePair<string, string>("keys.expiryDays", "7"),
        });
        _isLoading = true;
        LoadCurrentSelection();
        ClipToggle.IsOn = true;
        ThemeRadioButtons.SelectedItem = AutoThemeOption;
        _isLoading = false;
    }
}
