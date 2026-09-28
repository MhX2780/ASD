using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        this.AppWindow.Title = "ASD";

        // Start maximized using DisplayArea
        try
        {
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    displayArea.WorkArea.X,
                    displayArea.WorkArea.Y,
                    displayArea.WorkArea.Width,
                    displayArea.WorkArea.Height));
            }
        }
        catch { /* non-critical, window will show at default size */ }

        RootNav.PaneHeader = new Grid
        {
            Height = 48,
            Children =
            {
                new TextBlock
                {
                    Text = "ASD",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                }
            }
        };

        ApplyTheme(LoadSavedTheme());

        CursorHelper.Initialize(RootNav, Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors"));
    }

    /// <summary>
    /// Applies a theme: "auto" follows Windows, or "light" / "dark".
    /// </summary>
    public void ApplyTheme(string mode)
    {
        RootNav.RequestedTheme = mode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private static string LoadSavedTheme()
    {
        var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
        return values["ThemeMode"] as string ?? "auto";
    }

    private void RootNav_Loaded(object sender, RoutedEventArgs e)
    {
        RootNav.IsPaneOpen = false;
        RootNav.SelectedItem = RootNav.MenuItems[0];
        Navigate("encode");

        CursorHelper.ApplyHandCursorToNavItems(RootNav);
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        var targetType = tag switch
        {
            "encode" => typeof(EncodePage),
            "crypto" => typeof(CryptoPage),
            "hash" => typeof(HashPage),
            "text" => typeof(TextToolsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(EncodePage),
        };

        if (ContentFrame.Content?.GetType() == targetType) return;

        ContentFrame.Navigate(targetType);
    }
}
