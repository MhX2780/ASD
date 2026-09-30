using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using static ASD.ImageTools;

namespace ASD;

/// <summary>Converts PNG / JPG / BMP / GIF / TIFF / WebP / ICO … and SVG into a multi-size icon.ico.</summary>
public sealed class IcoPage : ToolPage
{
    private static readonly int[] AllSizes = { 16, 24, 32, 48, 64, 128, 256 };

    private readonly Dictionary<int, CheckBox> _sizeChecks = new();
    private readonly ComboBox _mode;
    private readonly Grid _host = new() { Width = 256, Height = 256 };
    private readonly Image _srcImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _fileLabel = new() { Text = "No image loaded yet", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _previews = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly Button _save;

    private byte[]? _srcPx;          // premultiplied BGRA of the loaded image
    private int _srcW, _srcH;
    private byte[]? _master;         // square premultiplied BGRA
    private int _side;
    private int _ver;

    public IcoPage() : base("ico", "Image → ICO",
        "Turn any image (PNG, JPG, BMP, GIF, TIFF, WebP, SVG …) into a multi-size icon.ico. Everything happens on your PC.")
    {
        var drop = new Border
        {
            Height = 90,
            AllowDrop = true,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new TextBlock
            {
                Text = "Drop an image here  (PNG · JPG · SVG · BMP · GIF · TIFF · WebP …)",
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
                if (items.Count > 0 && items[0] is StorageFile f) LoadFile(f.Path);
            }
            catch (Exception ex) { Fail(ex); }
        };

        _mode = Combo("Fit — keep the whole image (transparent padding)", "Fill — crop to a square");
        _mode.MinWidth = 380;
        _mode.SelectionChanged += (_, _) => OptionChanged();

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var s in AllSizes)
        {
            var cb = Check($"{s}", s != 24);
            cb.MinWidth = 60;
            cb.Checked += (_, _) => OptionChanged();
            cb.Unchecked += (_, _) => OptionChanged();
            _sizeChecks[s] = cb;
            sizeRow.Children.Add(cb);
        }

        _save = Btn("Save as icon.ico…", () => SaveClick(), true);
        _save.IsEnabled = false;

        _host.Children.Add(_srcImage);
        var hostFrame = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Child = _host,
        };

        Body.Children.Add(drop);
        Body.Children.Add(Row(Btn("Choose image…", () => PickFile()), _fileLabel));
        Body.Children.Add(Label("Icon sizes (px)"));
        Body.Children.Add(sizeRow);
        Body.Children.Add(Row(Label("Mode:"), _mode));
        Body.Children.Add(Label("Source"));
        Body.Children.Add(hostFrame);
        Body.Children.Add(Label("Icon preview (actual pixel sizes)"));
        Body.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = _previews,
        });
        Body.Children.Add(_save);
    }

    private List<int> SelectedSizes() => AllSizes.Where(s => _sizeChecks[s].IsChecked == true).ToList();

    // ───────── loading ─────────
    private async void PickFile()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".svg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico", ".heic", ".avif", ".jxr" })
                picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            if (file != null) LoadFile(file.Path);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void LoadFile(string path)
    {
        try
        {
            Status.Text = "Loading…";
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".svg" or ".svgz") await LoadSvg(path);
            else await LoadRaster(path);

            _fileLabel.Text = $"{Path.GetFileName(path)}  —  {_srcW}×{_srcH}px";
            _save.IsEnabled = true;
            await RebuildAsync();
            Status.Text = "Ready — pick the sizes you need, then press “Save as icon.ico…”.";
        }
        catch (Exception ex)
        {
            Status.Text = "Error: " + ex.Message + (ex is System.Runtime.InteropServices.COMException
                ? "  (this image format may not be supported on this PC)" : "");
        }
    }

    private async Task LoadRaster(string path)
        => (_srcPx, _srcW, _srcH) = await ImageTools.LoadRasterAsync(path);

    private async Task LoadSvg(string path)
        => (_srcPx, _srcW, _srcH) = await ImageTools.RenderSvgAsync(path, _host, _srcImage);

    // ───────── rebuilding previews ─────────
    private async void OptionChanged()
    {
        try { await RebuildAsync(); }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task RebuildAsync()
    {
        if (_srcPx == null) return;
        var v = ++_ver;
        var src = _srcPx;
        int w = _srcW, h = _srcH;
        bool fill = Sel(_mode).StartsWith("Fill");
        var sizes = SelectedSizes();

        var (master, side, previews) = await Task.Run(() =>
        {
            var m = BuildMaster(src, w, h, fill);
            var list = sizes.Select(s => (s, Resize(m.px, m.side, m.side, s, s))).ToList();
            return (m.px, m.side, list);
        });
        if (v != _ver) return;

        _master = master;
        _side = side;
        _srcImage.Source = MakeBitmap(master, side, side);

        _previews.Children.Clear();
        foreach (var (s, px) in previews)
        {
            var img = new Image { Width = s, Height = s, Stretch = Stretch.Fill, Source = MakeBitmap(px, s, s) };
            _previews.Children.Add(new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Bottom,
                Children =
                {
                    new Border { Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), Child = img },
                    new TextBlock { Text = $"{s}×{s}", FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }
        if (sizes.Count == 0) Status.Text = "Select at least one icon size.";
    }

    // ───────── saving ─────────
    private async void SaveClick()
    {
        try
        {
            if (_master == null) { Status.Text = "Load an image first."; return; }
            var sizes = SelectedSizes();
            if (sizes.Count == 0) { Status.Text = "Select at least one icon size."; return; }

            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "icon" };
            picker.FileTypeChoices.Add("Icon", new List<string> { ".ico" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            Status.Text = "Building icon…";
            var bytes = await BuildIco(_master, _side, sizes);
            await FileIO.WriteBytesAsync(file, bytes);
            Status.Text = $"Saved ✔  {file.Path}   ({bytes.Length / 1024.0:0.#} KB · sizes: {string.Join(", ", sizes)})";
        }
        catch (Exception ex) { Fail(ex); }
    }
}
