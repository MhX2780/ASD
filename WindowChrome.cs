using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ASD;

/// <summary>Title-bar helpers shared by the secondary windows (update / install).</summary>
internal static class WindowChrome
{
    /// <summary>
    /// Shows app.ico (copied next to ASD.exe by the project file) on the window's taskbar button.
    /// Unpackaged WinUI 3 windows don't pick the icon up by themselves. Does nothing if the file is missing.
    /// </summary>
    public static void ApplyTaskbarIcon(AppWindow appWindow)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) appWindow.SetTaskbarIcon(path);
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>Transparent min/max/close buttons whose icons match the light or dark theme (the Mica look).</summary>
    public static void ApplyCaptionColors(AppWindow appWindow, ElementTheme actualTheme)
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var light = actualTheme == ElementTheme.Light;
            var fg = light ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var hover = light ? Windows.UI.Color.FromArgb(25, 0, 0, 0) : Windows.UI.Color.FromArgb(25, 255, 255, 255);
            var none = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var tb = appWindow.TitleBar;
            tb.ButtonBackgroundColor = none;
            tb.ButtonInactiveBackgroundColor = none;
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
        }
        catch { /* cosmetic only */ }
    }
}
