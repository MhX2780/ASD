using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace ASD;

/// <summary>
/// Devices: pair a phone running ASD Connect. "Pair a device" starts the server (see <see cref="DeviceServer"/>) and shows a QR code;
/// the phones that are connected are listed here, each with a Disconnect button.
/// </summary>
public sealed class DevicesPage : ToolPage
{
    private readonly StackPanel _host = new() { Spacing = 12 };
    private readonly Button _stopButton;
    private ContentDialog? _dialog;
    private int _countAtOpen;
    private bool? _firewallOk;   // null = not looked at yet

    public DevicesPage() : base("devices", "Devices",
        "Pair your phone with ASD Connect: scan a QR code, then manage the connected devices here.")
    {
        _stopButton = ActionBtn("", "Stop sharing", () => DeviceServer.Stop());
        SetHeader("Devices", "Pair your phone with ASD Connect: scan a QR code, then manage the connected devices here.",
            ActionBtn("", "Pair a device", () => _ = PairAsync(), accent: true), _stopButton);
        Body.Children.Add(_host);

        Loaded += (_, _) =>
        {
            DeviceServer.Changed += OnServerChanged;
            Rebuild();
            if (DeviceServer.Running && _firewallOk == null) _ = CheckFirewallAsync();
        };
        Unloaded += (_, _) => DeviceServer.Changed -= OnServerChanged;
        Rebuild();
    }

    private void OnServerChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Rebuild();
            // a device connected while the QR code is shown: the pairing is done
            if (_dialog != null)
            {
                var devices = DeviceServer.Devices();
                if (devices.Count > _countAtOpen)
                {
                    Status.Text = $"Connected: {devices[^1].Name}";
                    _dialog.Hide();
                }
            }
        });
    }

    private async Task CheckFirewallAsync()
    {
        _firewallOk = await Task.Run(DeviceServer.FirewallRuleExists);
        Rebuild();
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        var running = DeviceServer.Running;
        _stopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        // the state of the server
        var state = new StackPanel { Spacing = 4 };
        state.Children.Add(new TextBlock { Text = running ? "Ready for devices" : "Not sharing", FontSize = 16, FontWeight = FontWeights.SemiBold });
        state.Children.Add(new TextBlock
        {
            Text = running
                ? $"Listening on port {DeviceServer.Port} (secure WebSocket)"
                : "Nothing is open on the network. Press “Pair a device” to start.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        _host.Children.Add(CardBox(state));

        if (running && _firewallOk == false) _host.Children.Add(CardBox(FirewallPanel()));

        // the connected devices
        var devices = DeviceServer.Devices();
        if (devices.Count == 0)
        {
            _host.Children.Add(EmptyState("", running
                ? "No device connected yet. Scan the QR code with ASD Connect."
                : "No device connected. Press “Pair a device” to show a QR code."));
            return;
        }
        foreach (var d in devices)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = "", FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = $"{d.Address} · connected at {d.Since:HH:mm}", Opacity = 0.7 });
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { icon, text } };
            var id = d.Id;
            var disconnect = Btn("Disconnect", () => DeviceServer.Disconnect(id));
            Grid.SetColumn(disconnect, 1);
            grid.Children.Add(left);
            grid.Children.Add(disconnect);
            _host.Children.Add(CardBox(grid));
        }
    }

    /// <summary>The warning that the Windows firewall may block phones, with a button that adds the rule.</summary>
    private StackPanel FirewallPanel()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Windows Firewall is probably blocking phones. Allow port {DeviceServer.Port} on private networks, and make sure your Wi-Fi is set to Private.",
            TextWrapping = TextWrapping.Wrap,
        });
        var allow = ActionBtn("", "Allow in firewall", () => _ = AllowFirewallAsync());
        var copy = CopyBtn("Copy command", () => DeviceServer.FirewallCommand);
        panel.Children.Add(Row(allow, copy));
        return panel;
    }

    private async Task AllowFirewallAsync()
    {
        var ok = await DeviceServer.AddFirewallRuleAsync();
        _firewallOk = ok;
        Status.Text = ok
            ? "Firewall rule added. Phones on your private network can connect now."
            : "The firewall rule was not added (permission was not given).";
        Rebuild();
    }

    // ───────── pairing ─────────

    private async Task PairAsync()
    {
        if (_dialog != null) return;
        try { DeviceServer.Start(); }
        catch (Exception ex)
        {
            Status.Text = ex is System.Net.Sockets.SocketException
                ? $"Could not start: port {DeviceServer.Port} is already used by another program."
                : $"Could not start: {ex.Message}";
            return;
        }

        var addresses = DeviceServer.LocalAddresses();
        if (addresses.Count == 0)
        {
            Status.Text = "There is no network connection. Connect this PC to the same Wi-Fi as your phone.";
            Rebuild();
            return;
        }

        _firewallOk = await Task.Run(DeviceServer.FirewallRuleExists);
        DeviceServer.RefreshToken();

        var panel = new StackPanel { Spacing = 12, Width = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = "Open ASD Connect on your phone and scan this code. The phone and this PC must be on the same Wi-Fi.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
        });

        ComboBox? chooser = null;
        if (addresses.Count > 1)
        {
            chooser = new ComboBox { Header = "This PC's address", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var a in addresses) chooser.Items.Add($"{a.Ip}   ({a.Interface})");
            chooser.SelectedIndex = 0;
            panel.Children.Add(chooser);
        }

        // a QR code is always dark on white, so every phone camera reads it in any theme
        var frame = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var qr = new Image { Width = 260, Height = 260, Stretch = Stretch.Uniform };
        frame.Child = qr;
        panel.Children.Add(frame);

        var countdown = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(countdown);

        string link = "";
        void Render()
        {
            var host = addresses[Math.Max(0, chooser?.SelectedIndex ?? 0)].Ip;
            link = DeviceServer.BuildLink(host);
            qr.Source = MakeQr(link);
        }
        void Tick()
        {
            var left = DeviceServer.PairingExpires - DateTime.UtcNow;
            countdown.Text = left > TimeSpan.Zero
                ? $"The code is valid for {(int)left.TotalMinutes}:{left.Seconds:00} and works once."
                : "The code expired. Press New code.";
        }
        if (chooser != null) chooser.SelectionChanged += (_, _) => Render();
        Render();
        Tick();

        panel.Children.Add(Row(CopyBtn("Copy link", () => link)));
        if (_firewallOk == false) panel.Children.Add(FirewallPanel());

        var dialog = MakeDialog("Pair a device");
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 620 };
        dialog.CloseButtonText = "Close";
        dialog.PrimaryButtonText = "New code";
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.PrimaryButtonClick += (_, a) =>
        {
            a.Cancel = true;   // the dialog stays open with a fresh code
            DeviceServer.RefreshToken();
            Render();
            Tick();
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();

        _countAtOpen = DeviceServer.Devices().Count;
        _dialog = dialog;
        Rebuild();
        try { await dialog.ShowAsync(); }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            timer.Stop();
            _dialog = null;
            DeviceServer.ClearToken();   // the QR code does not work after the window is closed
        }
    }

    /// <summary>The link as a QR code picture (black on white, with the quiet border).</summary>
    private static BitmapImage? MakeQr(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(12);
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var write = stream.AsStreamForWrite();
            write.Write(png, 0, png.Length);
            write.Flush();
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 520 };
            bitmap.SetSource(stream);
            return bitmap;
        }
        catch { return null; }
    }
}
