using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed class HashPage : ToolPage
{
    private static readonly (string Name, HashAlgorithmName Alg)[] Algs =
    {
        ("MD5", HashAlgorithmName.MD5),
        ("SHA-1", HashAlgorithmName.SHA1),
        ("SHA-256", HashAlgorithmName.SHA256),
        ("SHA-384", HashAlgorithmName.SHA384),
        ("SHA-512", HashAlgorithmName.SHA512),
    };

    private readonly TextBox _input, _key, _compare;
    private readonly ComboBox _format;
    private readonly TextBlock _source = new() { Opacity = 0.7, Text = "Source: text" };
    private readonly TextBox[] _plain = new TextBox[Algs.Length];
    private readonly TextBox[] _hmac = new TextBox[Algs.Length];
    private byte[][]? _lastPlain, _lastHmac;
    private string? _filePath;
    private int _ver;

    public HashPage() : base("hash", "Hash / HMAC", "Hash text or files. Enter a key to get HMAC values too. Nothing leaves your machine.")
    {
        _input = Input("text", "Text", 110, "Type or paste text to hash…");
        _key = Input("key", "HMAC key (optional, never saved)", persist: false, multiline: false);
        _compare = Input("cmp", "Compare with an expected hash (optional)", persist: false, multiline: false);
        _format = Combo("Hex (lowercase)", "Hex (UPPERCASE)", "Base64");

        var drop = new Border
        {
            Height = 64,
            AllowDrop = true,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new TextBlock
            {
                Text = "Drop a file here to hash it",
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        drop.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Copy;
        drop.Drop += async (_, e) =>
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is Windows.Storage.StorageFile f) UseFile(f.Path);
            }
            catch (Exception ex) { Fail(ex); }
        };

        Body.Children.Add(_input);
        Body.Children.Add(drop);
        Body.Children.Add(Row(Btn("Pick file…", PickFile), Btn("Back to text", () => { _filePath = null; _source.Text = "Source: text"; Recalc(); }), Label("Output:"), _format));
        Body.Children.Add(_source);
        Body.Children.Add(_key);

        var plainRows = new StackPanel { Spacing = 6 };
        var hmacRows = new StackPanel { Spacing = 6 };
        for (int i = 0; i < Algs.Length; i++)
        {
            _plain[i] = ResultRow(plainRows, Algs[i].Name, 110);
            _hmac[i] = ResultRow(hmacRows, "HMAC-" + Algs[i].Name.Replace("-", ""), 130);
        }
        Body.Children.Add(Label("Hash"));
        Body.Children.Add(plainRows);
        Body.Children.Add(Label("HMAC (needs a key)"));
        Body.Children.Add(hmacRows);
        Body.Children.Add(_compare);

        _input.TextChanged += (_, _) => { _filePath = null; _source.Text = "Source: text"; Recalc(); };
        _key.TextChanged += (_, _) => Recalc();
        _compare.TextChanged += (_, _) => CheckCompare();
        _format.SelectionChanged += (_, _) => Show();
        Recalc();
    }

    private async void PickFile()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file != null) UseFile(file.Path);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void UseFile(string path)
    {
        _filePath = path;
        _source.Text = "File: " + path;
        Recalc();
    }

    private async void Recalc()
    {
        var v = ++_ver;
        var path = _filePath;
        var text = Encoding.UTF8.GetBytes(_input.Text ?? "");
        var key = Encoding.UTF8.GetBytes(_key.Text ?? "");
        try
        {
            var (plain, hmac) = await Task.Run(() => HashAll(path, text, key));
            if (v != _ver) return;
            _lastPlain = plain;
            _lastHmac = hmac;
            Show();
        }
        catch (Exception ex)
        {
            if (v == _ver) Fail(ex);
        }
    }

    private static (byte[][] plain, byte[][]? hmac) HashAll(string? path, byte[] text, byte[] key)
    {
        var hs = Algs.Select(a => IncrementalHash.CreateHash(a.Alg)).ToArray();
        var hm = key.Length == 0 ? null : Algs.Select(a => IncrementalHash.CreateHMAC(a.Alg, key)).ToArray();
        try
        {
            void Feed(byte[] buf, int count)
            {
                foreach (var h in hs) h.AppendData(buf, 0, count);
                if (hm != null) foreach (var h in hm) h.AppendData(buf, 0, count);
            }

            if (path == null) Feed(text, text.Length);
            else
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                var buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) Feed(buf, n);
            }
            return (hs.Select(h => h.GetHashAndReset()).ToArray(), hm?.Select(h => h.GetHashAndReset()).ToArray());
        }
        finally
        {
            foreach (var h in hs) h.Dispose();
            if (hm != null) foreach (var h in hm) h.Dispose();
        }
    }

    private string Fmt(byte[] b) => Sel(_format) switch
    {
        "Hex (UPPERCASE)" => Convert.ToHexString(b),
        "Base64" => Convert.ToBase64String(b),
        _ => Convert.ToHexString(b).ToLowerInvariant(),
    };

    private void Show()
    {
        for (int i = 0; i < Algs.Length; i++)
        {
            _plain[i].Text = _lastPlain != null ? Fmt(_lastPlain[i]) : "";
            _hmac[i].Text = _lastHmac != null ? Fmt(_lastHmac[i]) : "";
        }
        CheckCompare();
    }

    private void CheckCompare()
    {
        var c = (_compare.Text ?? "").Trim();
        if (c.Length == 0 || _lastPlain == null) { Status.Text = ""; return; }
        bool ok = false;
        void Chk(byte[] b)
        {
            if (string.Equals(Convert.ToHexString(b), c, StringComparison.OrdinalIgnoreCase) || Convert.ToBase64String(b) == c) ok = true;
        }
        foreach (var b in _lastPlain) Chk(b);
        if (_lastHmac != null) foreach (var b in _lastHmac) Chk(b);
        Status.Text = ok ? "✔ Matches one of the computed hashes" : "✘ Does not match any computed hash";
    }
}
