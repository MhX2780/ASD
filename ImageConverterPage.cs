using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;

namespace ASD;

/// <summary>Convert between PNG, JPG, BMP, GIF, TIFF, ICO and SVG (single file or a whole batch).</summary>
public sealed class ImageConverterPage : ToolPage
{
    private static readonly int[] IcoSizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static readonly (string Label, string Key, string Ext)[] Targets =
    {
        ("PNG  (.png)", "png", ".png"),
        ("JPEG  (.jpg)", "jpg", ".jpg"),
        ("BMP  (.bmp)", "bmp", ".bmp"),
        ("GIF  (.gif)", "gif", ".gif"),
        ("TIFF  (.tif)", "tif", ".tif"),
        ("ICO  — multi-size icon (.ico)", "ico", ".ico"),
        ("SVG  — wraps the bitmap (.svg)", "svg", ".svg"),
    };

    private readonly ComboBox _target, _maxSize, _bg;
    private readonly NumberBox _quality, _tolerance;
    private readonly CheckBox _removeBg, _edgesOnly;
    private readonly StackPanel _optRemoveBg;
    private readonly Dictionary<int, CheckBox> _sizeChecks = new();
    private readonly StackPanel _optJpeg, _optIco;
    private readonly Grid _host = new() { Width = 200, Height = 200 };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _fileLabel = new() { Text = "No image chosen yet", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly List<string> _files = new();
    private readonly Button _convert;

    public ImageConverterPage() : base("imgconv", "Image Converter",
        "Convert between PNG, JPG, BMP, GIF, TIFF, ICO and SVG — one file or a whole batch. SVG files can be used as input too.")
    {
        var drop = new Border
        {
            Height = 80,
            AllowDrop = true,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new TextBlock
            {
                Text = "Drop one or more images here",
                Opacity = 0.75,
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
                SetFiles(items.OfType<StorageFile>().Select(f => f.Path).ToList());
            }
            catch (Exception ex) { Fail(ex); }
        };

        _target = Combo(Targets.Select(t => t.Label).ToArray());
        _target.MinWidth = 300;
        _maxSize = Combo("Original size", "1024", "512", "256", "128", "64", "32", "16");
        _maxSize.MinWidth = 150;
        _quality = new NumberBox { Header = "JPEG quality", Value = 90, Minimum = 1, Maximum = 100, Width = 150, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        _bg = Combo("White", "Black");
        _bg.Header = "Transparent areas become";
        _bg.MinWidth = 200;
        _optJpeg = Row(_quality, _bg);
        _optJpeg.Visibility = Visibility.Collapsed;

        _removeBg = Check("Remove the background (make it transparent)");
        _tolerance = new NumberBox { Header = "Tolerance %", Value = 15, Minimum = 0, Maximum = 100, Width = 150, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        _edgesOnly = Check("Only from the edges (keeps same-colour areas inside the picture)", true);
        _optRemoveBg = Row(_tolerance, _edgesOnly);
        _optRemoveBg.Visibility = Visibility.Collapsed;
        _removeBg.Checked += (_, _) => _optRemoveBg.Visibility = Visibility.Visible;
        _removeBg.Unchecked += (_, _) => _optRemoveBg.Visibility = Visibility.Collapsed;

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var s in IcoSizes)
        {
            var cb = Check($"{s}", s != 24);
            cb.MinWidth = 60;
            _sizeChecks[s] = cb;
            sizeRow.Children.Add(cb);
        }
        _optIco = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed, Children = { Label("Icon sizes (px)"), sizeRow } };

        _target.SelectionChanged += (_, _) =>
        {
            var key = Targets[Math.Max(0, _target.SelectedIndex)].Key;
            _optJpeg.Visibility = key == "jpg" ? Visibility.Visible : Visibility.Collapsed;
            _optIco.Visibility = key == "ico" ? Visibility.Visible : Visibility.Collapsed;
            Status.Text = key == "svg"
                ? "ℹ Raster → SVG embeds your bitmap inside an SVG file. It is not traced into vector shapes."
                : key == "gif" ? "ℹ GIF supports only 256 colors and 1-bit transparency."
                : "";
        };

        _convert = Btn("Convert and save…", () => ConvertClick(), true);
        _convert.IsEnabled = false;

        _host.Children.Add(_preview);
        var previewFrame = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Child = _host,
        };

        Body.Children.Add(drop);
        Body.Children.Add(Row(Btn("Choose images…", () => PickFiles()), _fileLabel));
        Body.Children.Add(previewFrame);
        Body.Children.Add(Row(Label("Convert to:"), _target, Label("Max side:"), _maxSize));
        Body.Children.Add(_removeBg);
        Body.Children.Add(_optRemoveBg);
        Body.Children.Add(_optJpeg);
        Body.Children.Add(_optIco);
        Body.Children.Add(_convert);
    }

    private static bool IsSvg(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".svg" or ".svgz";

    private async void PickFiles()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".svg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico", ".heic", ".avif", ".jxr" })
                picker.FileTypeFilter.Add(ext);
            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0) SetFiles(files.Select(f => f.Path).ToList());
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void SetFiles(List<string> paths)
    {
        try
        {
            _files.Clear();
            _files.AddRange(paths);
            _convert.IsEnabled = _files.Count > 0;
            if (_files.Count == 0) { _fileLabel.Text = "No image chosen yet"; return; }

            _fileLabel.Text = _files.Count == 1 ? Path.GetFileName(_files[0]) : $"{_files.Count} files selected";
            var first = _files[0];
            if (IsSvg(first))
            {
                var (_, w, h) = await ImageTools.RenderSvgAsync(first, _host, _preview);
                if (_files.Count == 1) _fileLabel.Text += "  (SVG)";
            }
            else
            {
                var (px, w, h) = await ImageTools.LoadRasterAsync(first);
                _preview.Source = ImageTools.MakeBitmap(px, w, h);
                if (_files.Count == 1) _fileLabel.Text += $"  —  {w}×{h}px";
            }
            Status.Text = "Ready — choose the output format, then press “Convert and save…”.";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not open the first image: " + ex.Message;
        }
    }

    private int MaxSide() => int.TryParse(Sel(_maxSize), out var m) ? m : 0;

    private async Task<byte[]> ConvertOne(string path, string key)
    {
        byte[] px;
        int w, h;
        if (IsSvg(path)) (px, w, h) = await ImageTools.RenderSvgAsync(path, _host, _preview);
        else (px, w, h) = await ImageTools.LoadRasterAsync(path);

        if (_removeBg.IsChecked == true)
        {
            int tol = double.IsNaN(_tolerance.Value) ? 15 : (int)_tolerance.Value;
            bool edges = _edgesOnly.IsChecked == true;
            var src0 = px; int w0 = w, h0 = h;
            px = await Task.Run(() => ImageTools.RemoveBackground(src0, w0, h0, tol, edges));
        }

        int max = MaxSide();
        if (max > 0 && Math.Max(w, h) > max)
        {
            double k = (double)max / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * k)), nh = Math.Max(1, (int)Math.Round(h * k));
            var src = px; int sw = w, sh = h;
            px = await Task.Run(() => ImageTools.Resize(src, sw, sh, nw, nh));
            w = nw; h = nh;
        }

        var sizes = IcoSizes.Where(s => _sizeChecks[s].IsChecked == true).ToList();
        if (key == "ico" && sizes.Count == 0) throw new InvalidOperationException("Select at least one icon size.");
        int quality = double.IsNaN(_quality.Value) ? 90 : (int)_quality.Value;
        return await ImageTools.EncodeAsync(key, px, w, h, quality, Sel(_bg) != "Black", sizes);
    }

    private async void ConvertClick()
    {
        try
        {
            if (_files.Count == 0) { Status.Text = "Choose one or more images first."; return; }
            var (label, key, ext) = Targets[Math.Max(0, _target.SelectedIndex)];
            if (_removeBg.IsChecked == true && key is "jpg" or "bmp")
            {
                Status.Text = "⚠ JPEG and BMP cannot store transparency. Choose PNG, ICO, GIF, TIFF or SVG.";
                return;
            }
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

            if (_files.Count == 1)
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(_files[0]) };
                picker.FileTypeChoices.Add(key.ToUpperInvariant(), new List<string> { ext });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;

                Status.Text = "Converting…";
                var bytes = await ConvertOne(_files[0], key);
                await FileIO.WriteBytesAsync(file, bytes);
                Status.Text = $"Saved ✔  {file.Path}   ({bytes.Length / 1024.0:0.#} KB)";
            }
            else
            {
                var fp = new Windows.Storage.Pickers.FolderPicker();
                fp.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(fp, hwnd);
                var folder = await fp.PickSingleFolderAsync();
                if (folder == null) return;

                int ok = 0;
                var errors = new List<string>();
                foreach (var f in _files)
                {
                    Status.Text = $"Converting {ok + errors.Count + 1} of {_files.Count}…";
                    try
                    {
                        var bytes = await ConvertOne(f, key);
                        var target = await folder.CreateFileAsync(Path.GetFileNameWithoutExtension(f) + ext, CreationCollisionOption.GenerateUniqueName);
                        await FileIO.WriteBytesAsync(target, bytes);
                        ok++;
                    }
                    catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
                }
                Status.Text = $"Converted {ok} of {_files.Count} → {folder.Path}"
                    + (errors.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", errors.Take(5)) : "");
            }
        }
        catch (Exception ex) { Fail(ex); }
    }
}
