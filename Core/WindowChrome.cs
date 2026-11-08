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

    /// <summary>The ASD icon to the left of a window's title (the title bar's own icon).</summary>
    public static void SetTitleIcon(Microsoft.UI.Xaml.Controls.TitleBar bar) => _ = SetTitleIconAsync(bar);

    private static async Task SetTitleIconAsync(Microsoft.UI.Xaml.Controls.TitleBar bar)
    {
        // the icon is app.ico itself (its biggest picture), so the title bar always matches the taskbar and the exe
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(ico))
            {
                using var file = File.OpenRead(ico);
                using var stream = file.AsRandomAccessStream();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                uint best = 0, width = 0;
                for (uint i = 0; i < decoder.FrameCount; i++)
                {
                    var frame = await decoder.GetFrameAsync(i);
                    if (frame.PixelWidth > width) { width = frame.PixelWidth; best = i; }
                }
                var chosen = await decoder.GetFrameAsync(best);
                using var bitmap = await chosen.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                bar.IconSource = new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = source };
                return;
            }
        }
        catch { /* use the splash picture below */ }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "SplashIcon.png");
            if (!File.Exists(path)) return;
            bar.IconSource = new Microsoft.UI.Xaml.Controls.ImageIconSource { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path)) };
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
