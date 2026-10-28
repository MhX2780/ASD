using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

/// <summary>The "enter your master password" dialog for tools outside the Keys Store page.</summary>
internal static class KeyUnlock
{
    /// <summary>
    /// true when the vault is unlocked afterwards (it may already have been). false when there is no vault or the
    /// person cancelled. The dialog follows the app's theme and offers Windows Hello when it is set up.
    /// </summary>
    public static async Task<bool> EnsureAsync(FrameworkElement owner, string reason)
    {
        if (KeyVault.IsUnlocked) { KeyVault.Touch(); return true; }
        if (!KeyVault.Exists) return false;

        var hello = KeyVault.HelloEnabled && await KeyVault.IsHelloSupportedAsync();

        var pw = new PasswordBox { PlaceholderText = "Master password", MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
        var err = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        };
        var ring = new ProgressRing { IsActive = false, Width = 22, Height = 22, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };

        var dlg = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,     // follow the app's own theme setting, not Windows
            Title = "Unlock Keys Store",
            CloseButtonText = "Cancel",
            PrimaryButtonText = hello ? "Windows Hello" : "Unlock",
            SecondaryButtonText = hello ? "Unlock" : "",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children = { new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }, pw, err, ring },
            },
        };

        void Busy(bool on)
        {
            ring.IsActive = on;
            ring.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            pw.IsEnabled = !on;
            dlg.IsPrimaryButtonEnabled = !on;
            dlg.IsSecondaryButtonEnabled = !on;
        }
        void Problem(string text) { err.Text = text; err.Visibility = Visibility.Visible; }

        async Task<bool> ByPassword()
        {
            var typed = pw.Password;
            if (typed.Length == 0) { Problem("Type your master password."); return false; }
            Busy(true);
            try
            {
                if (await Task.Run(() => KeyVault.UnlockWithPassword(typed))) return true;
                Problem("Wrong password.");
                pw.Password = "";
                return false;
            }
            catch (Exception ex) { Problem(ex.Message); return false; }
            finally { Busy(false); }
        }

        async Task<bool> ByHello()
        {
            Busy(true);
            try
            {
                if (await KeyVault.UnlockWithHelloAsync()) return true;
                Problem("Windows Hello did not unlock the vault. Use your master password.");
                return false;
            }
            catch (Exception ex) { Problem(ex.Message); return false; }
            finally { Busy(false); }
        }

        // the dialog stays open on a failure (a.Cancel) so the person can try again
        dlg.PrimaryButtonClick += async (_, a) =>
        {
            var deferral = a.GetDeferral();
            try { if (!await (hello ? ByHello() : ByPassword())) a.Cancel = true; }
            finally { deferral.Complete(); }
        };
        dlg.SecondaryButtonClick += async (_, a) =>
        {
            var deferral = a.GetDeferral();
            try { if (!await ByPassword()) a.Cancel = true; }
            finally { deferral.Complete(); }
        };
        if (hello)      // Enter types the password even though Windows Hello is the main button
        {
            pw.KeyDown += async (_, k) =>
            {
                if (k.Key != Windows.System.VirtualKey.Enter) return;
                k.Handled = true;
                if (await ByPassword()) dlg.Hide();
            };
        }
        dlg.Opened += (_, _) => { if (!hello) pw.Focus(FocusState.Programmatic); };

        await dlg.ShowAsync();
        return KeyVault.IsUnlocked;
    }
}
