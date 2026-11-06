using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace ASD;

/// <summary>
/// Automation: cards (cells) joined by coloured arrows, like a notebook that can run itself.
///   green arrow = "on success" (exit 0)   red arrow = "on failure" (exit not 0)
///   orange arrow = "when <command>" (the command decides)   grey arrow = "always"
/// Nothing runs when a file is opened. Start runs everything, a card's own buttons run only that cell (or from it on).
/// Left mouse drags a card; right mouse (or left on the empty canvas) drags the view; the dot on a card's right edge
/// is dragged onto another card to connect them. The document and the run live in <see cref="AutomationSession"/>.
/// </summary>
public sealed class AutomationPage : ToolPage
{
    private const double CardW = 240, CardH = 120;

    private static readonly Windows.UI.Color Green = ColorHelper.FromArgb(255, 34, 165, 90);
    private static readonly Windows.UI.Color Red = ColorHelper.FromArgb(255, 229, 72, 77);
    private static readonly Windows.UI.Color Orange = ColorHelper.FromArgb(255, 245, 158, 11);
    private static readonly Windows.UI.Color Blue = ColorHelper.FromArgb(255, 59, 130, 246);
    private static readonly Windows.UI.Color Grey = ColorHelper.FromArgb(255, 138, 138, 138);

    private const string ExampleText =
        "automation: Example\n\n" +
        "cell check:\n  title: Check website\n  note: GET example.com\n  http: GET https://example.com\n  success -> done\n  failure -> alert\n\n" +
        "cell done:\n  title: Finish\n  notify: The website is up\n  level: success\n\n" +
        "cell alert:\n  title: Alert\n  note: Something went wrong\n  notify: The website did not answer (status {{check.status}})\n  level: error\n";

    private enum Side { Right, Bottom, Left, Top }

    private sealed class CardView
    {
        public string Id = "";
        public Border Root = null!;
        public Button Handle = null!;
        public Side HandleSide = Side.Right;
        public TextBlock Status = null!;
        public ProgressRing Ring = null!;
        public Button Run = null!, RunFrom = null!, Cancel = null!;
    }

    private sealed class DragInfo
    {
        public string Id = "";
        public Point Start;
        public double X0, Y0;
        public bool Moved;
    }

    private sealed class PanInfo
    {
        public Point Start;
        public double H, V;
    }

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly ScrollViewer _scroll = new();
    private readonly Dictionary<string, CardView> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UIElement> _arrowElems = new();
    private readonly TextBlock _fileLabel = new() { Opacity = 0.7, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 150,
        FontFamily = new FontFamily("Consolas"), FontSize = 12,
    };
    private readonly Border _logPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _empty;

    // the minimap in the corner of the board, and the "breathing" border of a running cell
    private readonly Canvas _miniCanvas = new() { Width = 188, Height = 120 };
    private readonly Border _mini = new() { Visibility = Visibility.Collapsed };
    private double _miniScale = 1;
    private bool _miniDrag;
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _pulseLow;

    // toolbar: Save, Start and Add cell are split buttons (the click does the main thing, the arrow opens the rest), zoom is a drop-down
    private Button _btnUndo = null!, _btnStop = null!, _btnLog = null!;
    private SplitButton _btnSave = null!, _btnStart = null!, _btnAdd = null!;
    private DropDownButton _btnZoom = null!;
    private MenuFlyoutItem _btnNew = null!, _btnOpen = null!, _btnSaveAs = null!, _btnQuiet = null!, _btnConnect = null!, _btnSettings = null!,
                           _btnText = null!, _btnZoomIn = null!, _btnZoomOut = null!, _btnRunCell = null!, _btnRunFrom = null!;

    private string? _selected;
    private DragInfo? _drag;
    private PanInfo? _pan;
    private string? _linkFrom;
    private Point _linkStart;
    private bool _linkMoved;
    private Shapes.Path? _linkPath;
    private bool _dialogOpen;
    private bool _bannerShown;
    private bool _hooked;

    public AutomationPage() : base("automation", "Automation", "")
    {
        AutomationSession.Init(DispatcherQueue.GetForCurrentThread());

        _empty = EmptyState("", "No cells yet. Click Add cell, or open an .automation file.");
        var example = new Button { Content = "Create an example", HorizontalAlignment = HorizontalAlignment.Center };
        example.Click += (_, _) =>
        {
            if (!AutomationSession.SetText(ExampleText, out var errors, trusted: true)) Say(string.Join("\n", errors), InfoBarSeverity.Error);
        };
        _empty.Children.Add(example);

        BuildLayout();

        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Menu, () => _ = StartAsync(null, false, showLog: false));
        AddAccelerator(Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None, () => _ = StartAsync(null, false));
        AddAccelerator(Windows.System.VirtualKey.F2, Windows.System.VirtualKeyModifiers.None, () => { if (_selected != null) _ = OpenInspectorAsync(_selected); });
        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Control, () => { if (_selected != null) _ = StartAsync(_selected, true); });
        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Shift, () => { if (_selected != null) _ = StartAsync(_selected, false); });
        AddAccelerator(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, () => _ = SaveAsync(false));
        AddAccelerator(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () => _ = SaveAsync(true));
        AddAccelerator(Windows.System.VirtualKey.N, Windows.System.VirtualKeyModifiers.Control, () => _ = NewAsync());
        AddAccelerator(Windows.System.VirtualKey.O, Windows.System.VirtualKeyModifiers.Control, () => _ = OpenFileAsync());
        AddAccelerator(Windows.System.VirtualKey.L, Windows.System.VirtualKeyModifiers.Control, () => _ = OpenConnectAsync(null));
        AddAccelerator(Windows.System.VirtualKey.E, Windows.System.VirtualKeyModifiers.Control, () => _ = OpenTextAsync());

        _pulse.Tick += (_, _) => Pulse();
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => { Unhook(); _pulse.Stop(); };
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers mods, Action action)
    {
        var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
        acc.Invoked += (_, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(acc);
    }

    // ───────── layout ─────────

    private void BuildLayout()
    {
        _btnNew = Item("", "New", () => _ = NewAsync(), "Ctrl+N");
        _btnOpen = Item("", "Open", () => _ = OpenFileAsync(), "Ctrl+O");
        _btnSaveAs = Item("", "Save as", () => _ = SaveAsync(true), "Ctrl+Shift+S");
        _btnSave = Split("", "Save", () => _ = SaveAsync(false), false, "Save (Ctrl+S). The arrow: Save as, Open, New.", _btnSaveAs, new MenuFlyoutSeparator(), _btnOpen, _btnNew);

        _btnUndo = ActionBtn("", "Undo", () => AutomationSession.UndoLast());

        _btnQuiet = Item("", "Run quietly", () => _ = StartAsync(null, false, showLog: false), "Run everything without opening the log (Alt+Enter)");
        _btnRunCell = Item("", "Run selected cell only", () => { if (_selected != null) _ = StartAsync(_selected, true); }, "Ctrl+Enter");
        _btnRunFrom = Item("", "Run from selected cell", () => { if (_selected != null) _ = StartAsync(_selected, false); }, "Shift+Enter");
        _btnStart = Split("", "Start", () => _ = StartAsync(null, false), true, "Run the whole automation and show the log (F5). The arrow: run quietly, run one cell.", _btnQuiet, new MenuFlyoutSeparator(), _btnRunCell, _btnRunFrom);
        _btnStop = ActionBtn("", "Stop", () => AutomationSession.Stop());

        _btnConnect = Item("", "Connect cells", () => _ = OpenConnectAsync(null), "Pick the two cells from a list (Ctrl+L)");
        _btnSettings = Item("", "Cell settings", () => { if (_selected != null) _ = OpenInspectorAsync(_selected); }, "F2, or just click the card");
        _btnText = Item("", "Edit text", () => _ = OpenTextAsync(), "Edit the .automation text directly (Ctrl+E)");
        _btnAdd = Split("", "Add cell", () => _ = AddCellAsync(), false, "Add a card. The arrow: connect cells, settings, edit text.", _btnConnect, _btnSettings, _btnText);

        _btnZoomOut = Item("", "Zoom out", () => Zoom(1 / 1.25f));
        _btnZoomIn = Item("", "Zoom in", () => Zoom(1.25f));
        var zoomFit = Item("", "Fit everything", () => ZoomToFit());
        var zoomReset = Item("", "100%", () => ResetZoom());
        var zoomMenu = new MenuFlyout();
        foreach (var z in new MenuFlyoutItemBase[] { _btnZoomIn, _btnZoomOut, zoomReset, zoomFit }) zoomMenu.Items.Add(z);
        _btnZoom = new DropDownButton { Content = "100%", Flyout = zoomMenu };
        AutomationProperties.SetName(_btnZoom, "Zoom");
        ToolTipService.SetToolTip(_btnZoom, "Zoom. Ctrl + mouse wheel, or two fingers on the touchpad, also zoom.");

        _btnLog = ActionBtn("", "Log", () => ShowLog(_logPanel.Visibility != Visibility.Visible));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new UIElement[] { _btnSave, _btnUndo, Separator(), _btnStart, _btnStop, Separator(), _btnAdd, Separator(), _btnZoom, Separator(), _btnLog })
            bar.Children.Add(b);
        var barScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
            Content = bar, HorizontalAlignment = HorizontalAlignment.Right,
        };

        var titleBlock = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "Automation", FontSize = 26, FontWeight = FontWeights.Bold }, _fileLabel },
        };
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(barScroll, 1);
        header.Children.Add(titleBlock);
        header.Children.Add(barScroll);

        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollMode = ScrollMode.Enabled;
        _scroll.VerticalScrollMode = ScrollMode.Enabled;
        _scroll.ZoomMode = ZoomMode.Enabled;          // Ctrl + mouse wheel and a two-finger pinch on a touchpad zoom natively
        _scroll.MinZoomFactor = 0.3f;
        _scroll.MaxZoomFactor = 2f;
        _scroll.ViewChanged += (_, _) => { UpdateZoomLabel(); RefreshMiniMap(); };
        _scroll.IsTabStop = true;
        _scroll.Content = _canvas;
        _scroll.SizeChanged += (_, _) => UpdateCanvasSize();

        _canvas.AddHandler(PointerPressedEvent, new PointerEventHandler(Canvas_Pressed), true);
        _canvas.AddHandler(PointerMovedEvent, new PointerEventHandler(Canvas_Moved), true);
        _canvas.AddHandler(PointerReleasedEvent, new PointerEventHandler(Canvas_Released), true);
        _canvas.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Canvas_CaptureLost), true);

        var area = new Grid();
        area.Children.Add(_scroll);
        _empty.VerticalAlignment = VerticalAlignment.Center;
        area.Children.Add(_empty);
        _logPanel.VerticalAlignment = VerticalAlignment.Bottom;
        _logPanel.Margin = new Thickness(10);
        _logPanel.Padding = new Thickness(10);
        _logPanel.CornerRadius = new CornerRadius(8);
        _logPanel.BorderThickness = new Thickness(1);
        _logPanel.BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.5 };
        area.Children.Add(_logPanel);
        BuildMiniMap();
        area.Children.Add(_mini);
        var areaBox = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.4 },
            Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.06 },
            Child = area,
        };

        var hint = new TextBlock
        {
            Text = "Click a card: its settings · drag a card to move it · click the dot on a card (or the Connect button) to pick the cell it points to, or drag the dot onto a card · " +
                   "click an arrow's label to change it · right mouse or the empty canvas: move the view · Ctrl + wheel / pinch: zoom · " +
                   "F5 Start (with log) · Alt+Enter run quietly · Ctrl+Enter run this cell · Shift+Enter run from this cell · F2 settings",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };

        // log panel
        var copyLog = new Button { Content = "Copy log" };
        copyLog.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(AutomationSession.LogText.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Say("Log copied", InfoBarSeverity.Success);
        };
        var clearLog = new Button { Content = "Clear" };
        clearLog.Click += (_, _) => { AutomationSession.ClearLog(); _log.Text = ""; };
        var hideLog = new Button { Content = "Hide" };
        hideLog.Click += (_, _) => ShowLog(false);
        var logHead = new Grid();
        logHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { copyLog, clearLog, hideLog } };
        Grid.SetColumn(logButtons, 1);
        logHead.Children.Add(new TextBlock { Text = "Log", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        logHead.Children.Add(logButtons);
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        _logPanel.Child = new StackPanel { Spacing = 6, Children = { logHead, _log } };

        var root = new Grid { RowSpacing = 8, Padding = new Thickness(24, 16, 24, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_info, 1);
        Grid.SetRow(areaBox, 2);
        Grid.SetRow(hint, 3);
        root.Children.Add(header);
        root.Children.Add(_info);
        root.Children.Add(areaBox);
        root.Children.Add(hint);

        Content = root;
    }

    private static Border Separator() => new()
    {
        Width = 1, Height = 22, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.5 },
    };

    private void Say(string text, InfoBarSeverity severity)
    {
        _bannerShown = false;
        _info.Severity = severity;
        _info.Message = text;
        _info.IsOpen = true;
    }

    // ───────── connecting to the session ─────────

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        AutomationSession.OwnerElement = this;
        AutomationSession.Changed += OnDocChanged;
        AutomationSession.RunChanged += OnRunChanged;
        AutomationSession.LogAdded += OnLogAdded;
        AutomationSession.RunFinished += OnRunFinished;
        ActualThemeChanged += OnThemeChanged;

        var text = AutomationSession.LogText.ToString();
        _log.Text = text.Length > 200_000 ? text[^200_000..] : text;
        if (AutomationSession.Running || text.Length > 0) ShowLog(true);
        Rebuild();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        if (ReferenceEquals(AutomationSession.OwnerElement, this)) AutomationSession.OwnerElement = null;
        AutomationSession.Changed -= OnDocChanged;
        AutomationSession.RunChanged -= OnRunChanged;
        AutomationSession.LogAdded -= OnLogAdded;
        AutomationSession.RunFinished -= OnRunFinished;
        ActualThemeChanged -= OnThemeChanged;
    }

    private void OnDocChanged() => Rebuild();
    private void OnRunChanged() => RefreshRun();
    private void OnThemeChanged(FrameworkElement sender, object args) => Rebuild();

    private void OnLogAdded(string chunk)
    {
        _log.Text += chunk;
        if (_log.Text.Length > 300_000) _log.Text = _log.Text[^200_000..];
        ScrollLogToEnd();
    }

    private void OnRunFinished(NoteKind kind, string text)
    {
        var severity = kind switch
        {
            NoteKind.Success => InfoBarSeverity.Success,
            NoteKind.Error => InfoBarSeverity.Error,
            NoteKind.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        Say(text, severity);
    }

    private void ShowLog(bool show)
    {
        _logPanel.Background = ActualTheme == ElementTheme.Dark ? Solid(ColorHelper.FromArgb(245, 32, 32, 32)) : Solid(ColorHelper.FromArgb(245, 249, 249, 249));
        _logPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        try
        {
            _log.UpdateLayout();
            var sv = FindScroller(_log);
            sv?.ChangeView(null, sv.ScrollableHeight, null, true);
        }
        catch { /* cosmetic */ }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var deeper = FindScroller(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    // ───────── building the canvas ─────────

    private static SolidColorBrush Solid(Windows.UI.Color c, double opacity = 1) => new(c) { Opacity = opacity };

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _cards.Clear();
        _arrowElems.Clear();
        _linkPath = null;
        _drag = null;

        var doc = AutomationSession.Doc;
        foreach (var c in doc.Cells)
        {
            var v = MakeCard(c);
            _cards[c.Id] = v;
            _canvas.Children.Add(v.Root);
            _canvas.Children.Add(v.Handle);
            Canvas.SetZIndex(v.Root, 1);
            Canvas.SetZIndex(v.Handle, 2);
            Place(v, Math.Max(0, c.X), Math.Max(0, c.Y));
        }
        if (_selected != null && !_cards.ContainsKey(_selected)) _selected = null;

        _empty.Visibility = doc.Cells.Count == 0 && !AutomationSession.Invalid ? Visibility.Visible : Visibility.Collapsed;
        RefreshBanner();
        UpdateCanvasSize();
        RefreshRun();
        CursorHelper.ApplyHandCursorToButtons(_canvas);
    }

    private void RefreshBanner()
    {
        if (AutomationSession.Invalid)
        {
            var lines = AutomationSession.Errors.Take(5).ToList();
            var more = AutomationSession.Errors.Count > 5 ? $"\n… and {AutomationSession.Errors.Count - 5} more" : "";
            _info.Severity = InfoBarSeverity.Error;
            _info.Message = "This file has errors, so the canvas is empty. Click Text to read and fix it.\n" + string.Join("\n", lines) + more;
            _info.IsOpen = true;
            _bannerShown = true;
        }
        else if (_bannerShown)
        {
            _info.IsOpen = false;
            _bannerShown = false;
        }
    }

    private void RefreshFileLabel()
    {
        var s = AutomationSession.FilePath != null ? System.IO.Path.GetFileName(AutomationSession.FilePath) : "Untitled";
        if (AutomationSession.Dirty) s += "  •  not saved";
        if (AutomationSession.Running) s += "  •  running";
        _fileLabel.Text = s;
    }

    private void UpdateCanvasSize()
    {
        double w = 0, h = 0;
        foreach (var v in _cards.Values)
        {
            w = Math.Max(w, Canvas.GetLeft(v.Root) + CardW);
            h = Math.Max(h, Canvas.GetTop(v.Root) + CardH);
        }
        var z = Math.Max(0.1, _scroll.ZoomFactor);
        _canvas.Width = Math.Max(w + 400, Math.Max(_scroll.ViewportWidth / z, 600));
        _canvas.Height = Math.Max(h + 300, Math.Max(_scroll.ViewportHeight / z, 400));
        RefreshMiniMap();
    }

    // ───────── minimap ─────────

    private void BuildMiniMap()
    {
        _mini.HorizontalAlignment = HorizontalAlignment.Right;
        _mini.VerticalAlignment = VerticalAlignment.Top;
        _mini.Margin = new Thickness(0, 10, 18, 0);
        _mini.Padding = new Thickness(4);
        _mini.CornerRadius = new CornerRadius(8);
        _mini.BorderThickness = new Thickness(1);
        _mini.BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.5 };
        _mini.Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.18 };
        _mini.Child = _miniCanvas;
        _miniCanvas.Background = new SolidColorBrush(Colors.Transparent);     // so the canvas gets the pointer
        ToolTipService.SetToolTip(_mini, "Map of the board: click or drag to move the view");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_mini, "Board map");
        _miniCanvas.PointerPressed += (_, e) => { _miniDrag = true; _miniCanvas.CapturePointer(e.Pointer); MiniGo(e); };
        _miniCanvas.PointerMoved += (_, e) => { if (_miniDrag) MiniGo(e); };
        _miniCanvas.PointerReleased += (_, e) => { _miniDrag = false; _miniCanvas.ReleasePointerCapture(e.Pointer); };
        _miniCanvas.PointerCaptureLost += (_, _) => _miniDrag = false;
    }

    /// <summary>Puts the middle of the view where the pointer is on the map.</summary>
    private void MiniGo(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_miniCanvas).Position;
        var z = Math.Max(0.1, _scroll.ZoomFactor);
        var x = p.X / _miniScale * z - _scroll.ViewportWidth / 2;
        var y = p.Y / _miniScale * z - _scroll.ViewportHeight / 2;
        _scroll.ChangeView(Math.Max(0, x), Math.Max(0, y), null, true);
    }

    private void RefreshMiniMap()
    {
        if (_cards.Count == 0) { _mini.Visibility = Visibility.Collapsed; return; }
        double w = 0, h = 0;
        foreach (var v in _cards.Values)
        {
            w = Math.Max(w, Canvas.GetLeft(v.Root) + CardW);
            h = Math.Max(h, Canvas.GetTop(v.Root) + CardH);
        }
        w += 40; h += 40;
        var z = Math.Max(0.1, _scroll.ZoomFactor);
        var viewW = _scroll.ViewportWidth / z;
        var viewH = _scroll.ViewportHeight / z;

        // only worth showing when the board is bigger than what you see
        var needed = w > viewW * 1.05 || h > viewH * 1.05;
        _mini.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed) return;

        w = Math.Max(w, viewW); h = Math.Max(h, viewH);
        _miniScale = Math.Min(_miniCanvas.Width / w, _miniCanvas.Height / h);
        _miniCanvas.Children.Clear();
        foreach (var c in AutomationSession.Doc.Cells)
        {
            if (!_cards.TryGetValue(c.Id, out var v)) continue;
            AutomationSession.States.TryGetValue(c.Id, out var st);
            var color = st switch
            {
                CellState.Running => Blue, CellState.Succeeded => Green, CellState.Failed => Red,
                CellState.Cancelled => Orange, CellState.Skipped => Grey, _ => ColorHelper.FromArgb(255, 138, 138, 138),
            };
            _miniCanvas.Children.Add(new Border
            {
                Width = Math.Max(3, CardW * _miniScale), Height = Math.Max(3, CardH * _miniScale), CornerRadius = new CornerRadius(2),
                Background = Solid(color), Margin = new Thickness(Canvas.GetLeft(v.Root) * _miniScale, Canvas.GetTop(v.Root) * _miniScale, 0, 0),
            });
        }
        var vx = _scroll.HorizontalOffset / z * _miniScale;
        var vy = _scroll.VerticalOffset / z * _miniScale;
        _miniCanvas.Children.Add(new Border
        {
            Width = Math.Max(6, Math.Min(viewW * _miniScale, _miniCanvas.Width - vx)), Height = Math.Max(6, Math.Min(viewH * _miniScale, _miniCanvas.Height - vy)),
            BorderThickness = new Thickness(1.5), BorderBrush = Solid(ColorHelper.FromArgb(255, 96, 165, 250)),
            Background = Solid(ColorHelper.FromArgb(255, 96, 165, 250), 0.12), CornerRadius = new CornerRadius(2),
            Margin = new Thickness(vx, vy, 0, 0),
        });
    }

    // ───────── a running cell "breathes" ─────────

    private void Pulse()
    {
        var any = false;
        _pulseLow = !_pulseLow;
        foreach (var c in AutomationSession.Doc.Cells)
        {
            AutomationSession.States.TryGetValue(c.Id, out var st);
            if (st != CellState.Running || !_cards.TryGetValue(c.Id, out var v)) continue;
            any = true;
            if (v.Root.BorderBrush is SolidColorBrush b) b.Opacity = _pulseLow ? 0.35 : 1.0;
        }
        if (!any) { _pulse.Stop(); _pulseLow = false; }
    }

    private static MenuFlyoutItem Item(string glyph, string text, Action click, string? tip = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => click();
        if (tip != null) ToolTipService.SetToolTip(item, tip);
        return item;
    }

    private SplitButton Split(string glyph, string text, Action primary, bool accent, string tip, params MenuFlyoutItemBase[] items)
    {
        var button = new SplitButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } },
            },
        };
        if (accent) MakeAccent(button);
        var flyout = new MenuFlyout();
        foreach (var i in items) flyout.Items.Add(i);
        button.Flyout = flyout;
        button.Click += (_, _) =>
        {
            try { primary(); }
            catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
        };
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    /// <summary>The system's accent (primary) color for a split button: the ready-made style when this version has one, and the same colors set by hand either way.</summary>
    private static void MakeAccent(SplitButton button)
    {
        var res = Application.Current.Resources;
        if (res.TryGetValue("AccentSplitButtonStyle", out var st) && st is Style style) button.Style = style;

        object? Res(string key) => res.TryGetValue(key, out var v) ? v : null;
        var normal = Res("AccentFillColorDefaultBrush");
        var hover = Res("AccentFillColorSecondaryBrush") ?? normal;
        var pressed = Res("AccentFillColorTertiaryBrush") ?? normal;
        var disabled = Res("AccentFillColorDisabledBrush") ?? normal;
        var text = Res("TextOnAccentFillColorPrimaryBrush");
        var textDisabled = Res("TextOnAccentFillColorDisabledBrush") ?? text;
        var none = Res("ControlFillColorTransparentBrush");

        void Set(string key, object? value) { if (value != null) button.Resources[key] = value; }
        foreach (var part in new[] { "SplitButton", "SplitButtonPrimary", "SplitButtonSecondary" })
        {
            Set(part + "Background", normal);
            Set(part + "BackgroundPointerOver", hover);
            Set(part + "BackgroundPressed", pressed);
            Set(part + "BackgroundDisabled", disabled);
            Set(part + "Foreground", text);
            Set(part + "ForegroundPointerOver", text);
            Set(part + "ForegroundPressed", text);
            Set(part + "ForegroundDisabled", textDisabled);
            Set(part + "BorderBrush", normal);
            Set(part + "BorderBrushPointerOver", hover);
            Set(part + "BorderBrushPressed", pressed);
            Set(part + "BorderBrushDisabled", disabled);
        }
        Set("SplitButtonDividerBrush", Res("TextOnAccentFillColorPrimaryBrush"));
        if (normal is Brush nb) button.Background = nb;
        if (text is Brush tb) button.Foreground = tb;
    }

    private void ZoomToFit()
    {
        if (_cards.Count == 0) { ResetZoom(); return; }
        double w = 0, h = 0;
        foreach (var v in _cards.Values)
        {
            w = Math.Max(w, Canvas.GetLeft(v.Root) + CardW);
            h = Math.Max(h, Canvas.GetTop(v.Root) + CardH);
        }
        var z = (float)Math.Clamp(Math.Min(_scroll.ViewportWidth / (w + 40), _scroll.ViewportHeight / (h + 40)), _scroll.MinZoomFactor, 1.0);
        _scroll.ChangeView(0, 0, z, true);
    }

    private void UpdateZoomLabel() => _btnZoom.Content = $"{(int)Math.Round(_scroll.ZoomFactor * 100)}%";

    private void Zoom(float factor)
    {
        var z0 = _scroll.ZoomFactor;
        var z = Math.Clamp(z0 * factor, _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
        var cx = (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) / z0;      // keep the middle of what you see in the middle
        var cy = (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) / z0;
        _scroll.ChangeView(Math.Max(0, cx * z - _scroll.ViewportWidth / 2), Math.Max(0, cy * z - _scroll.ViewportHeight / 2), z, true);
    }

    private void ResetZoom() => _scroll.ChangeView(null, null, 1f, true);

    private static string StepSummary(AutoCell c) => c.Step switch
    {
        StepKind.Run => "> " + Flat(c.Command),
        StepKind.Http => c.HttpMethod + " " + Flat(c.HttpUrl),
        StepKind.Notify => "notify: " + Flat(c.Command),
        StepKind.Wait => $"wait {c.WaitSeconds:0.##} s",
        StepKind.Status => "status: result of the cell before",
        StepKind.Upload => "upload " + Flat(System.IO.Path.GetFileName(c.UploadFile.Replace('/', '\\'))) + " → " + Flat(c.UploadTo),
        _ => "(no step: passes through)",
    };

    private static string Flat(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private static Button SmallBtn(string glyph, string tip, string name, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 4, 8, 4), MinHeight = 0, MinWidth = 0,
        };
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    private CardView MakeCard(AutoCell c)
    {
        var id = c.Id;
        var dark = ActualTheme == ElementTheme.Dark;
        var v = new CardView { Id = id };

        var title = new TextBlock { Text = c.Display, FontWeight = FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        var note = new TextBlock
        {
            Text = c.Note, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            Visibility = c.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var step = new TextBlock
        {
            Text = StepSummary(c), FontFamily = new FontFamily("Consolas"), FontSize = 12, Opacity = 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(0, 4, 0, 0),
        };
        v.Ring = new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
        v.Status = new TextBlock { FontSize = 12, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(8, 0, 0, 0) };
        v.Run = SmallBtn("", "Run this cell only (Ctrl+Enter)", "Run cell " + id, () => _ = StartAsync(id, true));
        v.RunFrom = SmallBtn("", "Run from this cell on (Shift+Enter)", "Run from cell " + id, () => _ = StartAsync(id, false));
        v.Cancel = SmallBtn("", "Cancel this cell", "Cancel cell " + id, () => AutomationSession.CancelCell(id));

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(v.Ring, 1);
        top.Children.Add(title);
        top.Children.Add(v.Ring);

        var bottom = new Grid { ColumnSpacing = 4 };
        for (var i = 0; i < 3; i++) bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(v.RunFrom, 1);
        Grid.SetColumn(v.Cancel, 2);
        Grid.SetColumn(v.Status, 3);
        bottom.Children.Add(v.Run);
        bottom.Children.Add(v.RunFrom);
        bottom.Children.Add(v.Cancel);
        bottom.Children.Add(v.Status);

        var grid = new Grid();
        for (var i = 0; i < 5; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Grid.SetRow(top, 0);
        Grid.SetRow(note, 1);
        Grid.SetRow(step, 2);
        Grid.SetRow(bottom, 4);
        grid.Children.Add(top);
        grid.Children.Add(note);
        grid.Children.Add(step);
        grid.Children.Add(bottom);

        v.Root = new Border
        {
            Width = CardW, Height = CardH, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2),
            Padding = new Thickness(12, 8, 12, 8), Tag = id, Child = grid,
            Background = dark ? Solid(ColorHelper.FromArgb(255, 43, 43, 43)) : Solid(ColorHelper.FromArgb(255, 255, 255, 255)),
        };
        ToolTipService.SetToolTip(v.Root, $"{c.Display}  (id: {c.Id})\n{StepSummary(c)}\nClick for settings · drag to move");

        var ring = dark ? Solid(ColorHelper.FromArgb(255, 43, 43, 43)) : Solid(ColorHelper.FromArgb(255, 255, 255, 255));
        var dot = Solid(Grey);
        v.Handle = new Button
        {
            Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Tag = id,
            CornerRadius = new CornerRadius(7), Background = dot, BorderThickness = new Thickness(2), BorderBrush = ring,
        };
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
            v.Handle.Resources[key] = dot;
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            v.Handle.Resources[key] = ring;
        ToolTipService.SetToolTip(v.Handle, "Drag onto another cell to connect them with an arrow");
        AutomationProperties.SetName(v.Handle, "Connect from " + id);
        v.Handle.AddHandler(PointerPressedEvent, new PointerEventHandler(Handle_Pressed), true);
        v.Handle.AddHandler(PointerMovedEvent, new PointerEventHandler(Handle_Moved), true);
        v.Handle.AddHandler(PointerReleasedEvent, new PointerEventHandler(Handle_Released), true);
        v.Handle.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Handle_CaptureLost), true);
        return v;
    }

    private static void Place(CardView v, double x, double y)
    {
        Canvas.SetLeft(v.Root, x);
        Canvas.SetTop(v.Root, y);
        PlaceHandle(v);
    }

    private static Point AnchorOf(double x, double y, Side side, double off) => side switch
    {
        Side.Right => new Point(x + CardW, y + CardH / 2 + off),
        Side.Left => new Point(x, y + CardH / 2 + off),
        Side.Bottom => new Point(x + CardW / 2 + off, y + CardH),
        _ => new Point(x + CardW / 2 + off, y),
    };

    private static (double X, double Y) NormalOf(Side side) => side switch
    {
        Side.Right => (1, 0),
        Side.Left => (-1, 0),
        Side.Bottom => (0, 1),
        _ => (0, -1),
    };

    private static Side Opposite(Side side) => side switch { Side.Right => Side.Left, Side.Left => Side.Right, Side.Bottom => Side.Top, _ => Side.Bottom };

    /// <summary>Which side of a card faces a point: right/left when the target is mostly beside it, bottom/top when it is mostly above or below.</summary>
    private static Side SideToward(double fromX, double fromY, double toX, double toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        if (Math.Abs(dx) / CardW >= Math.Abs(dy) / CardH) return dx >= 0 ? Side.Right : Side.Left;
        return dy >= 0 ? Side.Bottom : Side.Top;
    }

    private static void PlaceHandle(CardView v)
    {
        var p = AnchorOf(Canvas.GetLeft(v.Root), Canvas.GetTop(v.Root), v.HandleSide, 0);
        Canvas.SetLeft(v.Handle, p.X - 7);
        Canvas.SetTop(v.Handle, p.Y - 7);
    }

    private void RefreshRun()
    {
        var doc = AutomationSession.Doc;
        foreach (var c in doc.Cells)
            if (_cards.TryGetValue(c.Id, out var v)) ApplyState(v, c);
        DrawArrows();
        RefreshToolbar();
        RefreshFileLabel();
        RefreshMiniMap();
    }

    private void ApplyState(CardView v, AutoCell c)
    {
        AutomationSession.States.TryGetValue(c.Id, out var st);
        AutomationSession.Exits.TryGetValue(c.Id, out var exit);
        var running = AutomationSession.Running;
        var selected = _selected != null && c.Id.Equals(_selected, StringComparison.OrdinalIgnoreCase);

        Windows.UI.Color color;
        var status = "";
        var opacity = 1.0;
        switch (st)
        {
            case CellState.Running: color = Blue; status = "running…"; break;
            case CellState.Succeeded: color = Green; status = "exit " + exit; break;
            case CellState.Failed: color = Red; status = "exit " + exit; break;
            case CellState.Skipped: color = Grey; status = "skipped"; opacity = 0.55; break;
            case CellState.Cancelled: color = Orange; status = "cancelled"; break;
            default: color = selected ? ColorHelper.FromArgb(255, 96, 165, 250) : ColorHelper.FromArgb(160, 138, 138, 138); break;
        }
        v.Root.BorderBrush = Solid(color);
        v.Root.BorderThickness = new Thickness(selected || st == CellState.Running ? 3 : 2);
        if (st == CellState.Running && !_pulse.IsEnabled) _pulse.Start();
        v.Root.Opacity = opacity;
        v.Status.Text = status;
        v.Ring.IsActive = st == CellState.Running;
        v.Ring.Visibility = st == CellState.Running ? Visibility.Visible : Visibility.Collapsed;
        v.Run.IsEnabled = !running;
        v.RunFrom.IsEnabled = !running;
        v.Cancel.IsEnabled = st == CellState.Running;
    }

    private void Select(string? id)
    {
        _selected = id;
        foreach (var c in AutomationSession.Doc.Cells)
            if (_cards.TryGetValue(c.Id, out var v)) ApplyState(v, c);
        RefreshToolbar();
        _scroll.Focus(FocusState.Programmatic);
    }

    private void RefreshToolbar()
    {
        var s = AutomationSession.Running;
        var ok = !AutomationSession.Invalid && AutomationSession.Doc.Cells.Count > 0;
        _btnStart.IsEnabled = !s && ok;
        _btnQuiet.IsEnabled = !s && ok;
        _btnStop.IsEnabled = s;
        _btnUndo.IsEnabled = AutomationSession.CanUndo && !s;
        _btnNew.IsEnabled = !s;
        _btnOpen.IsEnabled = !s;
        _btnSave.IsEnabled = !AutomationSession.Invalid;
        _btnSaveAs.IsEnabled = !AutomationSession.Invalid;
        _btnAdd.IsEnabled = !s && !AutomationSession.Invalid;
        _btnText.IsEnabled = !s;
        _btnConnect.IsEnabled = !s && !AutomationSession.Invalid && AutomationSession.Doc.Cells.Count >= 2;
        _btnSettings.IsEnabled = _selected != null && !s;
        _btnRunCell.IsEnabled = _selected != null && !s && ok;
        _btnRunFrom.IsEnabled = _selected != null && !s && ok;
    }

    // ───────── arrows ─────────

    private static Windows.UI.Color ArrowColor(AutoArrow a)
    {
        var custom = AutoArrow.ParseColor(a.Color);
        if (custom != null) return ColorHelper.FromArgb(255, custom.Value.R, custom.Value.G, custom.Value.B);
        if (a.When != null) return Orange;
        if (a.ExitCode != null || a.Contains != null) return Blue;
        return a.Kind switch { ArrowKind.Success => Green, ArrowKind.Failure => Red, _ => Grey };
    }

    /// <summary>White or black text, whichever is easier to read on this arrow color.</summary>
    private static Windows.UI.Color TextOn(Windows.UI.Color c)
        => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 160 ? Colors.Black : Colors.White;

    private static string Short(string text, int max)
    {
        var t = Flat(text);
        return t.Length > max ? t[..max] + "…" : t;
    }

    private static string BadgeText(AutoArrow a)
    {
        if (!string.IsNullOrWhiteSpace(a.Name)) return Short(a.Name, 30);
        if (a.Otherwise) return "otherwise";
        var extra = a.When != null || a.ExitCode != null || a.Contains != null;
        var parts = new List<string>();
        if (a.Kind != ArrowKind.Always || !extra) parts.Add(a.Kind switch { ArrowKind.Success => "success", ArrowKind.Failure => "failure", _ => "always" });
        if (a.ExitCode != null) parts.Add("exit " + a.ExitCode.Value);
        if (a.Contains != null) parts.Add("contains " + Short(a.Contains, 18));
        if (a.When != null) parts.Add("when " + Short(a.When, 26));
        return string.Join(" · ", parts);
    }

    /// <summary>One arrow, planned before drawing: which side it leaves and enters, and how far along that side.</summary>
    private sealed class ArrowPlan
    {
        public AutoCell Cell = null!;
        public CardView From = null!, To = null!;
        public AutoArrow Arrow = null!;
        public int Index;
        public Side Out, In;
        public double OutOff, InOff;
    }

    private static double CenterAlong(CardView v, Side side)
        => side is Side.Right or Side.Left ? Canvas.GetTop(v.Root) + CardH / 2 : Canvas.GetLeft(v.Root) + CardW / 2;

    /// <summary>Arrows that use the same side of a card are spread along that side (in the order of where they go), so they never run on top of each other.</summary>
    private static void Spread(IEnumerable<IGrouping<(string, Side), ArrowPlan>> groups, bool outgoing)
    {
        foreach (var g in groups)
        {
            var side = g.Key.Item2;
            var list = g.OrderBy(p => CenterAlong(outgoing ? p.To : p.From, side)).ThenBy(p => p.Index).ToList();
            var len = side is Side.Right or Side.Left ? CardH : CardW;
            var step = Math.Min(34, (len - 50) / Math.Max(1, list.Count - 1));
            for (var k = 0; k < list.Count; k++)
            {
                var off = (k - (list.Count - 1) / 2.0) * step;
                if (outgoing) list[k].OutOff = off; else list[k].InOff = off;
            }
        }
    }

    private void DrawArrows(bool applyCursor = true)
    {
        foreach (var el in _arrowElems) _canvas.Children.Remove(el);
        _arrowElems.Clear();

        // the connecting dot sits on the side that faces the cell the first arrow points to (right when there is none)
        foreach (var c in AutomationSession.Doc.Cells)
        {
            if (!_cards.TryGetValue(c.Id, out var hv)) continue;
            var side = Side.Right;
            var first = c.Out.FirstOrDefault();
            if (first != null && _cards.TryGetValue(first.To, out var ft))
                side = SideToward(Canvas.GetLeft(hv.Root) + CardW / 2, Canvas.GetTop(hv.Root) + CardH / 2, Canvas.GetLeft(ft.Root) + CardW / 2, Canvas.GetTop(ft.Root) + CardH / 2);
            hv.HandleSide = side;
            PlaceHandle(hv);
        }

        var plans = new List<ArrowPlan>();
        foreach (var c in AutomationSession.Doc.Cells)
        {
            if (!_cards.TryGetValue(c.Id, out var from)) continue;
            for (var i = 0; i < c.Out.Count; i++)
            {
                if (!_cards.TryGetValue(c.Out[i].To, out var to)) continue;
                var outSide = SideToward(Canvas.GetLeft(from.Root) + CardW / 2, Canvas.GetTop(from.Root) + CardH / 2, Canvas.GetLeft(to.Root) + CardW / 2, Canvas.GetTop(to.Root) + CardH / 2);
                plans.Add(new ArrowPlan { Cell = c, From = from, To = to, Arrow = c.Out[i], Index = i, Out = outSide, In = Opposite(outSide) });
            }
        }
        Spread(plans.GroupBy(p => (p.Cell.Id.ToLowerInvariant(), p.Out)), true);
        Spread(plans.GroupBy(p => (p.Arrow.To.ToLowerInvariant(), p.In)), false);
        _badgeRects.Clear();
        foreach (var plan in plans) DrawArrow(plan);
        if (applyCursor) CursorHelper.ApplyHandCursorToButtons(_canvas);
    }

    private readonly List<Rect> _badgeRects = new();

    /// <summary>True when the curve runs through a card other than the two it connects.</summary>
    private bool CrossesCard(Point p0, Point p1, Point p2, Point p3, string fromId, string toId)
    {
        foreach (var (id, v) in _cards)
        {
            if (id.Equals(fromId, StringComparison.OrdinalIgnoreCase) || id.Equals(toId, StringComparison.OrdinalIgnoreCase)) continue;
            var x = Canvas.GetLeft(v.Root) - 10;
            var y = Canvas.GetTop(v.Root) - 10;
            for (var i = 1; i < 20; i++)
            {
                var q = BezierAt(p0, p1, p2, p3, i / 20.0);
                if (q.X >= x && q.X <= x + CardW + 20 && q.Y >= y && q.Y <= y + CardH + 20) return true;
            }
        }
        return false;
    }

    private static Point BezierAt(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private void DrawArrow(ArrowPlan plan)
    {
        var c = plan.Cell;
        var from = plan.From;
        var to = plan.To;
        var a = plan.Arrow;
        var index = plan.Index;

        // leave through the side that faces the target and enter through the opposite side: a cell below gets an arrow going down.
        // Arrows that share a side were already spread along it (plan.OutOff / plan.InOff).
        var sx = Canvas.GetLeft(from.Root);
        var sy = Canvas.GetTop(from.Root);
        var tx = Canvas.GetLeft(to.Root);
        var ty = Canvas.GetTop(to.Root);
        var outSide = plan.Out;
        var inSide = plan.In;
        var p0 = AnchorOf(sx, sy, outSide, plan.OutOff);
        var p3 = AnchorOf(tx, ty, inSide, plan.InOff);
        var reach = Math.Clamp(Math.Sqrt((p3.X - p0.X) * (p3.X - p0.X) + (p3.Y - p0.Y) * (p3.Y - p0.Y)) / 2.5, 50, 160);
        var n0 = NormalOf(outSide);
        var n1 = NormalOf(inSide);
        var p1 = new Point(p0.X + n0.X * reach, p0.Y + n0.Y * reach);
        var p2 = new Point(p3.X + n1.X * reach, p3.Y + n1.Y * reach);

        // a cell in the way (e.g. A -> C with B between them): bend the arrow around it instead of drawing it behind the card
        if (CrossesCard(p0, p1, p2, p3, c.Id, a.To))
        {
            var dx = p3.X - p0.X;
            var dy = p3.Y - p0.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > 1)
            {
                var nx = -dy / dist;
                var ny = dx / dist;
                foreach (var bulge in new[] { 70.0, -70.0, 120.0, -120.0, 180.0, -180.0, 260.0, -260.0 })
                {
                    var b1 = new Point(p1.X + nx * bulge, p1.Y + ny * bulge);
                    var b2 = new Point(p2.X + nx * bulge, p2.Y + ny * bulge);
                    if (CrossesCard(p0, b1, b2, p3, c.Id, a.To)) continue;
                    p1 = b1;
                    p2 = b2;
                    break;
                }
            }
        }

        var color = ArrowColor(a);
        bool? fired = AutomationSession.Arrows.TryGetValue((c.Id.ToLowerInvariant(), index), out var f) ? f : null;
        var thickness = fired == true ? 4.0 : 2.5;
        var opacity = fired == false ? 0.25 : 1.0;

        var figure = new PathFigure { StartPoint = p0, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var path = new Shapes.Path
        {
            Data = geometry, Stroke = Solid(color), StrokeThickness = thickness, Opacity = opacity, IsHitTestVisible = false,
        };

        var vx = p3.X - p2.X;
        var vy = p3.Y - p2.Y;
        var len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1) { vx = 1; vy = 0; len = 1; }
        vx /= len; vy /= len;
        var px = -vy;
        var py = vx;
        var head = new Shapes.Polygon
        {
            Fill = Solid(color), Opacity = opacity, IsHitTestVisible = false,
            Points = new PointCollection
            {
                p3,
                new Point(p3.X - 13 * vx + 6 * px, p3.Y - 13 * vy + 6 * py),
                new Point(p3.X - 13 * vx - 6 * px, p3.Y - 13 * vy - 6 * py),
            },
        };

        var fill = Solid(color);
        var none = Solid(Colors.Transparent);
        var badge = new Button
        {
            Content = new TextBlock { Text = BadgeText(a), FontSize = 11, Foreground = Solid(TextOn(color)), MaxLines = 1 },
            Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, MinWidth = 0, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0), Background = fill, Opacity = opacity,
        };
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled" })
            badge.Resources[key] = fill;
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            badge.Resources[key] = none;
        ToolTipService.SetToolTip(badge, $"{a.Label} → {a.To}\nClick to change this arrow");
        AutomationProperties.SetName(badge, "Arrow " + BadgeText(a) + " to " + a.To);
        var fromId = c.Id;
        badge.Click += (_, _) => _ = OpenArrowEditorAsync(fromId, index);
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // the label sits on the line, at the first spot where it does not cover another label
        var bw = badge.DesiredSize.Width;
        var bh = badge.DesiredSize.Height;
        Point mid = BezierAt(p0, p1, p2, p3, 0.5);
        foreach (var t in new[] { 0.5, 0.38, 0.62, 0.28, 0.72, 0.2, 0.8 })
        {
            var cand = BezierAt(p0, p1, p2, p3, t);
            var r = new Rect(cand.X - bw / 2 - 3, cand.Y - bh / 2 - 3, bw + 6, bh + 6);
            if (_badgeRects.Any(o => r.X < o.X + o.Width && o.X < r.X + r.Width && r.Y < o.Y + o.Height && o.Y < r.Y + r.Height)) continue;
            mid = cand;
            break;
        }
        _badgeRects.Add(new Rect(mid.X - bw / 2 - 3, mid.Y - bh / 2 - 3, bw + 6, bh + 6));
        Canvas.SetLeft(badge, mid.X - bw / 2);
        Canvas.SetTop(badge, mid.Y - bh / 2);

        Canvas.SetZIndex(path, 0);
        Canvas.SetZIndex(head, 0);
        Canvas.SetZIndex(badge, 3);
        foreach (var el in new UIElement[] { path, head, badge })
        {
            _canvas.Children.Add(el);
            _arrowElems.Add(el);
        }
    }

    // ───────── mouse: cards, connecting, panning ─────────

    private void Handle_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var h = (FrameworkElement)sender;
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (AutomationSession.Running) { Say("Stop the run before changing arrows.", InfoBarSeverity.Warning); return; }
        _linkFrom = (string)h.Tag;
        _linkStart = e.GetCurrentPoint(_canvas).Position;
        _linkMoved = false;
        h.CapturePointer(e.Pointer);
        _linkPath = new Shapes.Path
        {
            Stroke = Solid(Grey), StrokeThickness = 2, IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        Canvas.SetZIndex(_linkPath, 4);
        _canvas.Children.Add(_linkPath);
        Select(_linkFrom);
    }

    private void Handle_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_linkFrom == null || _linkPath == null || !_cards.TryGetValue(_linkFrom, out var v)) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        if (!_linkMoved && Math.Abs(p.X - _linkStart.X) < 4 && Math.Abs(p.Y - _linkStart.Y) < 4) return;
        _linkMoved = true;
        var x0 = Canvas.GetLeft(v.Handle) + 7;
        var y0 = Canvas.GetTop(v.Handle) + 7;
        var nrm = NormalOf(v.HandleSide);
        var reach = Math.Max(40, Math.Sqrt((p.X - x0) * (p.X - x0) + (p.Y - y0) * (p.Y - y0)) / 2.5);
        var figure = new PathFigure { StartPoint = new Point(x0, y0), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = new Point(x0 + nrm.X * reach, y0 + nrm.Y * reach), Point2 = p, Point3 = p });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _linkPath.Data = geometry;
    }

    private void Handle_Released(object sender, PointerRoutedEventArgs e)
    {
        var from = _linkFrom;
        var moved = _linkMoved;
        EndLink();
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (from == null) return;
        if (!moved) { _ = OpenConnectAsync(from); e.Handled = true; return; }   // a click on the dot: choose the target from a list
        var target = CardAt(e.GetCurrentPoint(_canvas).Position);
        if (target != null && !target.Equals(from, StringComparison.OrdinalIgnoreCase)) ConnectCells(from, target);
        e.Handled = true;
    }

    private void Handle_CaptureLost(object sender, PointerRoutedEventArgs e) => EndLink();

    private void EndLink()
    {
        _linkFrom = null;
        if (_linkPath != null) { _canvas.Children.Remove(_linkPath); _linkPath = null; }
    }

    private string? CardAt(Point p)
    {
        foreach (var (id, v) in _cards)
        {
            var x = Canvas.GetLeft(v.Root);
            var y = Canvas.GetTop(v.Root);
            if (p.X >= x && p.X <= x + CardW && p.Y >= y && p.Y <= y + CardH) return id;
        }
        return null;
    }

    private void ConnectCells(string from, string to, ArrowKind? forced = null, bool custom = false, bool otherwise = false)
    {
        var chosen = ArrowKind.Success;
        var ok = AutomationSession.TryEdit(f =>
        {
            var c = f.Find(from) ?? throw new InvalidOperationException("the cell is gone");
            var target = f.Find(to) ?? throw new InvalidOperationException("the target cell is gone");
            chosen = forced ?? (!c.Out.Any(a => a.Kind == ArrowKind.Success) ? ArrowKind.Success
                   : !c.Out.Any(a => a.Kind == ArrowKind.Failure) ? ArrowKind.Failure
                   : ArrowKind.Always);
            if (!custom && c.Out.Any(a => a.Kind == chosen && a.Otherwise == otherwise && a.IsPlain && a.To.Equals(target.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That arrow already exists.");
            c.Out.Add(new AutoArrow { Kind = chosen, Otherwise = otherwise, To = target.Id });
        }, out var error);

        if (ok)
        {
            var word = otherwise ? "otherwise (the else branch)" : chosen switch { ArrowKind.Success => "success (green)", ArrowKind.Failure => "failure (red)", _ => "always (grey)" };
            Say($"Arrow added: {word}. Click its label to change when it fires.", InfoBarSeverity.Informational);
            if (custom)
            {
                var n = AutomationSession.Doc.Find(from)?.Out.Count ?? 0;
                if (n > 0) _ = OpenArrowEditorAsync(from, n - 1);   // your own condition, color and name
            }
        }
        else Say(error, InfoBarSeverity.Warning);
    }

    /// <summary>The card (cell id) that a pointer event started on, or null (empty canvas, or a button inside a card).</summary>
    private string? CardOf(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && !ReferenceEquals(d, _canvas))
        {
            if (d is Button) return null;
            if (d is Border b && b.Tag is string id && _cards.TryGetValue(id, out var v) && ReferenceEquals(v.Root, b)) return id;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // One place handles every press on the canvas: a card (move it, or open its settings when it was only clicked),
    // the right mouse button or the empty canvas (move the view).
    private void Canvas_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        if (pt.Properties.IsLeftButtonPressed)
        {
            var id = CardOf(e.OriginalSource);
            if (id != null && _cards.TryGetValue(id, out var v))
            {
                _drag = new DragInfo { Id = id, Start = pt.Position, X0 = Canvas.GetLeft(v.Root), Y0 = Canvas.GetTop(v.Root) };
                Select(id);
                _canvas.CapturePointer(e.Pointer);
                return;
            }
        }

        var ps = e.GetCurrentPoint(_scroll);
        var onEmpty = ps.Properties.IsLeftButtonPressed && ReferenceEquals(e.OriginalSource, _canvas);
        if (!ps.Properties.IsRightButtonPressed && !onEmpty) return;
        _pan = new PanInfo { Start = ps.Position, H = _scroll.HorizontalOffset, V = _scroll.VerticalOffset };
        _canvas.CapturePointer(e.Pointer);
        if (onEmpty) Select(null);
    }

    private void Canvas_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != null)
        {
            if (!_cards.TryGetValue(_drag.Id, out var v)) return;
            var p = e.GetCurrentPoint(_canvas).Position;
            var dx = p.X - _drag.Start.X;
            var dy = p.Y - _drag.Start.Y;
            if (!_drag.Moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
            _drag.Moved = true;
            Place(v, Math.Max(0, Math.Round((_drag.X0 + dx) / 10) * 10), Math.Max(0, Math.Round((_drag.Y0 + dy) / 10) * 10));
            DrawArrows(applyCursor: false);
            return;
        }

        if (_pan == null) return;
        var q = e.GetCurrentPoint(_scroll).Position;
        _scroll.ChangeView(_pan.H - (q.X - _pan.Start.X), _pan.V - (q.Y - _pan.Start.Y), null, true);
    }

    private void FinishMove(DragInfo d)
    {
        if (_cards.TryGetValue(d.Id, out var v))
            AutomationSession.MoveCell(d.Id, (int)Canvas.GetLeft(v.Root), (int)Canvas.GetTop(v.Root));
        UpdateCanvasSize();
        DrawArrows();
        RefreshFileLabel();
    }

    private void Canvas_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != null)
        {
            var d = _drag;
            _drag = null;
            _canvas.ReleasePointerCapture(e.Pointer);
            if (d.Moved) FinishMove(d);
            else _ = OpenInspectorAsync(d.Id);          // a click on a card shows its settings
            return;
        }
        if (_pan == null) return;
        _pan = null;
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    private void Canvas_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pan = null;
        if (_drag is { Moved: true } d) { _drag = null; FinishMove(d); }
    }

    // ───────── dialogs ─────────

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private static TextBox Field(string header, string text, bool multi = false, double height = 0, bool mono = true)
    {
        var tb = new TextBox
        {
            Header = header.Length > 0 ? header : null, Text = text, AcceptsReturn = multi, IsSpellCheckEnabled = false,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (mono) tb.FontFamily = new FontFamily("Consolas");
        if (multi && height > 0) tb.Height = height;
        if (multi) ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
        return tb;
    }

    private static NumberBox Number(string header, double value, double max)
        => new() { Header = header, Minimum = 0, Maximum = max, Value = value, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 160 };

    private static double Num(NumberBox box) => double.IsNaN(box.Value) ? 0 : box.Value;

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    /// <summary>One arrow in a list: type, target, and the optional own conditions, text and color.</summary>
    private sealed class ArrowRow
    {
        public readonly Border Root = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 10) };
        public readonly ComboBox Kind = new() { MinWidth = 190, Header = "Fires when the cell ends" };
        public readonly TextBox When = new() { Header = "Only if this command exits 0 (optional; reads ASD_EXIT_CODE and ASD_OUTPUT)", PlaceholderText = "e.g. powershell -c \"exit 0\"", IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Stretch };
        public readonly TextBox ExitCode = new() { Header = "Only if exit code is", PlaceholderText = "any", Width = 150, IsSpellCheckEnabled = false };
        public readonly TextBox Contains = new() { Header = "Only if the output contains (optional)", PlaceholderText = "text, letter case ignored", IsSpellCheckEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        public readonly TextBox Name = new() { Header = "Text on the arrow (optional)", PlaceholderText = "e.g. Retry", HorizontalAlignment = HorizontalAlignment.Stretch };
        public readonly TextBox Color = new() { Header = "Color (#RRGGBB or a name; empty = automatic)", PlaceholderText = "#8B5CF6", Width = 230, IsSpellCheckEnabled = false };
        public readonly ComboBox Target = new() { MinWidth = 150, Header = "Goes to" };
        public readonly Button Delete = new() { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Bottom };

        private static readonly (string Name, string Hex)[] Swatches =
        {
            ("green", "#22A55A"), ("red", "#E5484D"), ("orange", "#F59E0B"), ("yellow", "#EAB308"), ("teal", "#14B8A6"),
            ("blue", "#3B82F6"), ("purple", "#8B5CF6"), ("pink", "#EC4899"), ("grey", "#8A8A8A"),
        };

        public ArrowRow(IEnumerable<string> targets, AutoArrow? a, Action<ArrowRow>? onDelete)
        {
            Kind.Items.Add("On success (exit 0)");
            Kind.Items.Add("On failure (exit not 0)");
            Kind.Items.Add("Always");
            Kind.Items.Add("Otherwise (none of the other arrows of this cell fired)");
            Kind.SelectedIndex = a == null ? 0 : a.Otherwise ? 3 : a.Kind switch { ArrowKind.Success => 0, ArrowKind.Failure => 1, _ => 2 };
            foreach (var t in targets) Target.Items.Add(t);
            if (a != null && Target.Items.Contains(a.To)) Target.SelectedItem = a.To;
            else if (Target.Items.Count > 0) Target.SelectedIndex = 0;
            When.Text = a?.When ?? "";
            ExitCode.Text = a?.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Contains.Text = a?.Contains ?? "";
            Name.Text = a?.Name ?? "";
            Color.Text = a?.Color ?? "";
            ToolTipService.SetToolTip(Delete, "Delete this arrow");
            AutomationProperties.SetName(Delete, "Delete this arrow");
            AutomationProperties.SetName(Color, "Arrow color");
            AutomationProperties.SetName(Name, "Arrow text");

            var top = new Grid { ColumnSpacing = 8 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Target, 1);
            Grid.SetColumn(Delete, 2);
            top.Children.Add(Kind);
            top.Children.Add(Target);
            if (onDelete != null) { top.Children.Add(Delete); Delete.Click += (_, _) => onDelete(this); }
            Target.HorizontalAlignment = HorizontalAlignment.Stretch;

            var conditions = new Grid { ColumnSpacing = 8 };
            conditions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            conditions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(Contains, 1);
            conditions.Children.Add(ExitCode);
            conditions.Children.Add(Contains);

            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var (name, hex) in Swatches)
            {
                var c = AutoArrow.ParseColor(hex)!.Value;
                var brush = new SolidColorBrush(ColorHelper.FromArgb(255, c.R, c.G, c.B));
                var sw = new Button { Width = 24, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(12), Background = brush, BorderThickness = new Thickness(0) };
                foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" }) sw.Resources[key] = brush;
                ToolTipService.SetToolTip(sw, name);
                AutomationProperties.SetName(sw, "Color " + name);
                var chosen = hex;
                sw.Click += (_, _) => Color.Text = chosen;
                swatches.Children.Add(sw);
            }
            var clear = new Button { Content = "Automatic", Padding = new Thickness(8, 2, 8, 2), MinHeight = 0 };
            clear.Click += (_, _) => Color.Text = "";
            swatches.Children.Add(clear);

            var look = new Grid { ColumnSpacing = 8 };
            look.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            look.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Color, 1);
            look.Children.Add(Name);
            look.Children.Add(Color);

            Root.Child = new StackPanel { Spacing = 8, Children = { top, When, conditions, look, swatches } };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var stroke) && stroke is Brush b) Root.BorderBrush = b;
            else Root.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(90, 128, 128, 128));
        }

        public AutoArrow ToArrow()
        {
            var to = Target.SelectedItem as string ?? "";
            if (to.Length == 0) throw new InvalidOperationException("An arrow has no target cell.");
            var when = When.Text.Trim();
            int? code = null;
            var codeText = ExitCode.Text.Trim();
            if (codeText.Length > 0)
            {
                if (!int.TryParse(codeText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                    throw new InvalidOperationException($"\"{codeText}\" is not a number: write the exit code, e.g. 2, or leave the box empty.");
                code = n;
            }
            var colorText = Color.Text.Trim();
            string? color = null;
            if (colorText.Length > 0)
            {
                color = AutoArrow.NormalizeColor(colorText);
                if (color == null) throw new InvalidOperationException($"\"{colorText}\" is not a color: write it like #8B5CF6, or a name such as orange, blue, purple.");
            }
            var contains = Contains.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            var name = Name.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            var otherwise = Kind.SelectedIndex == 3;
            if (otherwise) { when = ""; code = null; contains = ""; }       // "otherwise" has no conditions of its own
            return new AutoArrow
            {
                Kind = Kind.SelectedIndex switch { 0 => ArrowKind.Success, 1 => ArrowKind.Failure, _ => ArrowKind.Always },
                Otherwise = otherwise,
                When = when.Length > 0 ? when : null,
                ExitCode = code,
                Contains = contains.Length > 0 ? contains : null,
                Color = color,
                Name = name.Length > 0 ? name : null,
                To = to,
            };
        }
    }

    /// <summary>Puts a dialog's content in a scroll box that is never taller than the window, so nothing is cut off.</summary>
    private ScrollViewer Scrollable(UIElement content)
    {
        var h = XamlRoot?.Size.Height ?? 800;
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, h - 230),
            Padding = new Thickness(0, 0, 16, 0),
        };
    }

    private static string CellLabel(AutoCell c) => c.Title.Length > 0 && !c.Title.Equals(c.Id, StringComparison.OrdinalIgnoreCase) ? $"{c.Title}  ({c.Id})" : c.Id;

    /// <summary>Connect two cells without dragging: pick the "from" cell, then click the cell the arrow should point to.</summary>
    private async Task OpenConnectAsync(string? fromId)
    {
        if (_dialogOpen) return;
        if (AutomationSession.Running) { Say("Stop the run before changing arrows.", InfoBarSeverity.Warning); return; }
        var doc = AutomationSession.Doc;
        if (doc.Cells.Count < 2) { Say("You need at least two cells to connect them. Click Add cell first.", InfoBarSeverity.Informational); return; }
        fromId ??= _selected ?? doc.Cells[0].Id;
        var start = Math.Max(0, doc.Cells.FindIndex(c => c.Id.Equals(fromId, StringComparison.OrdinalIgnoreCase)));

        var cbFrom = new ComboBox { Header = "From (the result of this cell decides the arrow)", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in doc.Cells) cbFrom.Items.Add(CellLabel(c));
        cbFrom.SelectedIndex = start;
        var cbKind = new ComboBox { Header = "Arrow type", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in new[] { "Automatic (green first, then red, then grey)", "On success (green)", "On failure (red)", "Always (grey)", "Custom… (your own condition, color and name)", "Otherwise (the else branch)" }) cbKind.Items.Add(k);
        cbKind.SelectedIndex = 0;

        var list = new StackPanel { Spacing = 6 };
        string? target = null;
        var dlg = MakeDialog("Connect cells");

        void Fill()
        {
            list.Children.Clear();
            var from = doc.Cells[Math.Max(0, cbFrom.SelectedIndex)];
            foreach (var c in doc.Cells.Where(x => !ReferenceEquals(x, from)))
            {
                var id = c.Id;
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = c.Display + "   (" + c.Id + ")", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = StepSummary(c), Opacity = 0.7, FontSize = 12, FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
                        },
                    },
                };
                AutomationProperties.SetName(button, "Connect to " + id);
                button.Click += (_, _) => { target = id; dlg.Hide(); };
                list.Children.Add(button);
            }
            CursorHelper.ApplyHandCursorToButtons(list);
        }
        cbFrom.SelectionChanged += (_, _) => Fill();
        Fill();

        dlg.Content = Scrollable(new StackPanel
        {
            Spacing = 10, MinWidth = 520,
            Children =
            {
                cbFrom, cbKind,
                new TextBlock { Text = "Click the cell the arrow should point to:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                list,
            },
        });
        dlg.CloseButtonText = "Cancel";
        dlg.Opened += (_, _) => CursorHelper.ApplyHandCursorToButtons(list);
        await ShowAsync(dlg);

        if (target == null) return;
        ArrowKind? kind = cbKind.SelectedIndex switch { 1 => ArrowKind.Success, 2 => ArrowKind.Failure, 3 or 4 or 5 => ArrowKind.Always, _ => null };
        ConnectCells(doc.Cells[Math.Max(0, cbFrom.SelectedIndex)].Id, target, kind, custom: cbKind.SelectedIndex == 4, otherwise: cbKind.SelectedIndex == 5);
    }

    private async Task OpenInspectorAsync(string id)
    {
        if (_dialogOpen) return;
        if (AutomationSession.Running) { Say("Stop the run to change a cell.", InfoBarSeverity.Warning); return; }
        var cell = AutomationSession.Doc.Find(id);
        if (cell == null) return;
        var oldId = cell.Id;

        var tbId = Field("Id (arrows and {{id.exitcode}} use it; letters, digits, - and _)", cell.Id, mono: false);
        var tbTitle = Field("Title (big text on the card)", cell.Title, mono: false);
        var tbNote = Field("Mini title (small text under the title)", cell.Note, mono: false);

        var cbStep = new ComboBox { Header = "What this cell does", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { "Nothing (only passes through, e.g. a Finish marker)", "Run a command", "HTTP request", "Notification", "Wait", "Status (takes the result of the cell before it; its arrows decide by that)", "Upload a file (Dropbox or HTTP)" }) cbStep.Items.Add(s);
        cbStep.SelectedIndex = cell.Step switch { StepKind.Run => 1, StepKind.Http => 2, StepKind.Notify => 3, StepKind.Wait => 4, StepKind.Status => 5, StepKind.Upload => 6, _ => 0 };

        // run
        var tbCmd = Field("Command (runs with cmd.exe, under your Windows account)", cell.Step == StepKind.Run ? cell.Command : "", multi: true, height: 70);
        var pRun = new StackPanel { Spacing = 6, Children = { tbCmd, KeyRefs.InsertButton(() => tbCmd), Note("The command's exit code decides the arrows: 0 = success, anything else = failure. Read an earlier cell's output from the environment variable ASD_<ID>_OUTPUT.") } };

        // http
        var cbMethod = new ComboBox { Header = "Method", MinWidth = 130 };
        foreach (var m in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }) cbMethod.Items.Add(m);
        cbMethod.SelectedItem = cell.HttpMethod;
        if (cbMethod.SelectedIndex < 0) cbMethod.SelectedIndex = 0;
        var tbUrl = Field("URL", cell.HttpUrl, mono: true);
        var tbHeaders = Field("Headers (one per line: Name: value)", string.Join("\r\n", cell.Headers.Select(h => h.Key + ": " + h.Value)), multi: true, height: 70);
        var tbBody = Field("Body (optional)", cell.Body, multi: true, height: 70);
        var pHttp = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                cbMethod, tbUrl, KeyRefs.InsertButton(() => tbUrl), tbHeaders, KeyRefs.InsertButton(() => tbHeaders), tbBody, KeyRefs.InsertButton(() => tbBody),
                Note("Success = a 2xx answer (exit 0). Any other answer or an error = failure; the status code is {{id.status}}."),
            },
        };

        // notify
        var tbText = Field("Text of the notification", cell.Step == StepKind.Notify ? cell.Command : "", multi: true, height: 70, mono: false);
        var cbLevel = new ComboBox { Header = "Kind", MinWidth = 150 };
        foreach (var l in new[] { "info", "success", "warning", "error" }) cbLevel.Items.Add(l);
        cbLevel.SelectedItem = cell.Level;
        if (cbLevel.SelectedIndex < 0) cbLevel.SelectedIndex = 0;
        var pNotify = new StackPanel { Spacing = 6, Children = { tbText, cbLevel, Note("You can use {{id.exitcode}}, {{id.status}} and {{id.output}} of earlier cells in the text.") } };

        // wait
        var nbWait = Number("Seconds to wait", cell.WaitSeconds, 86400);
        var pWait = new StackPanel { Children = { nbWait } };

        // status
        var pStatus = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Note("A status cell does nothing itself: it takes the exit code, the output and the status of the cell that sent it here. " +
                     "Give it several arrows and each fires by that result, for example  exit 0 → B,  exit 1 → C,  otherwise → D. " +
                     "The arrow that points at it should be \"Always\" if it must also receive a failure."),
            },
        };

        // upload
        var upKind = (cell.UploadTo.Length > 0 ? cell.UploadTo.Split(' ', 2)[0] : "dropbox").ToLowerInvariant();
        var upRest = cell.UploadTo.Contains(' ') ? cell.UploadTo[(cell.UploadTo.IndexOf(' ') + 1)..].Trim() : "";
        var tbUpFile = Field("File to upload (full path; {{id.output}} of an earlier cell can be the path)", cell.UploadFile, mono: true);
        var btnUpBrowse = new Button { Content = "Browse…" };
        btnUpBrowse.Click += async (_, _) =>
        {
            var path = await FilePick.AnyAsync();
            if (path != null) tbUpFile.Text = path;
        };
        var cbUpTo = new ComboBox { Header = "Send it to", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var t in new[] { "Dropbox", "HTTP POST (multipart form, field \"file\")", "HTTP PUT (the file is the body)" }) cbUpTo.Items.Add(t);
        cbUpTo.SelectedIndex = upKind switch { "post" => 1, "put" => 2, _ => 0 };
        var tbUpPath = Field("Dropbox path (a folder ending with /, or the full file path) or the URL", upRest, mono: true);
        var tbUpHeaders = Field("Headers for HTTP (one per line: Name: value)", string.Join("\r\n", cell.Headers.Select(h => h.Key + ": " + h.Value)), multi: true, height: 70);
        var pUpload = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                tbUpFile, btnUpBrowse, cbUpTo, tbUpPath, KeyRefs.InsertButton(() => tbUpPath), tbUpHeaders, KeyRefs.InsertButton(() => tbUpHeaders),
                Note("Success = the file was sent (exit 0). The result is {{id.status}} (the HTTP status) and {{id.output}} (the answer, or the Dropbox share link). " +
                     "Dropbox needs a one-time sign-in: open the Upload page and use its Dropbox menu."),
            },
        };

        var nbTimeout = Number("Time limit in seconds (0 = default)", cell.TimeoutSeconds, 86400);
        var cbJoin = new ComboBox { Header = "When several arrows point at this cell", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var j in new[] { "Wait for all of them; run if at least one fired", "Wait for all of them; run only if every one fired", "Run as soon as the first one fires" }) cbJoin.Items.Add(j);
        cbJoin.SelectedIndex = cell.Join switch { JoinMode.All => 1, JoinMode.First => 2, _ => 0 };

        void SyncPanels()
        {
            var i = cbStep.SelectedIndex;
            pRun.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            pHttp.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            pNotify.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
            pWait.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
            pStatus.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
            pUpload.Visibility = i == 6 ? Visibility.Visible : Visibility.Collapsed;
            nbTimeout.Visibility = i is 1 or 2 or 6 ? Visibility.Visible : Visibility.Collapsed;
        }
        cbStep.SelectionChanged += (_, _) => SyncPanels();
        SyncPanels();

        // arrows out of this cell
        var targets = AutomationSession.Doc.Cells.Select(c => c.Id).Where(x => !x.Equals(oldId, StringComparison.OrdinalIgnoreCase)).ToList();
        var rowsPanel = new StackPanel { Spacing = 6 };
        var rows = new List<ArrowRow>();
        void AddRow(AutoArrow? a)
        {
            var row = new ArrowRow(targets, a, r => { rows.Remove(r); rowsPanel.Children.Remove(r.Root); });
            rows.Add(row);
            rowsPanel.Children.Add(row.Root);
        }
        foreach (var a in cell.Out) AddRow(a);
        var addArrow = new Button { Content = "Add arrow", IsEnabled = targets.Count > 0 };
        addArrow.Click += (_, _) =>
        {
            var hasSuccess = rows.Any(r => r.Kind.SelectedIndex == 0);
            var row = new ArrowRow(targets, null, r => { rows.Remove(r); rowsPanel.Children.Remove(r.Root); });
            row.Kind.SelectedIndex = hasSuccess ? 1 : 0;
            rows.Add(row);
            rowsPanel.Children.Add(row.Root);
            CursorHelper.ApplyHandCursorToButtons(rowsPanel);
        };

        var error = new TextBlock { Foreground = Solid(Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var content = new StackPanel
        {
            Spacing = 10, MinWidth = 600,
            Children =
            {
                tbId, tbTitle, tbNote, cbStep, pRun, pHttp, pNotify, pWait, pStatus, pUpload, nbTimeout, cbJoin,
                new TextBlock { Text = "Arrows out of this cell", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                Note("Green = on success · red = on failure · grey = always. Your own conditions (all of them must be true): an exit code, a text in the output, a command that exits 0 (orange). Give the arrow your own text and color if you like."),
                rowsPanel, addArrow, error,
            },
        };

        var dlg = MakeDialog("Cell: " + cell.Display);
        dlg.Content = Scrollable(content);
        dlg.PrimaryButtonText = "Apply";
        dlg.SecondaryButtonText = "Delete cell";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;

        dlg.PrimaryButtonClick += (_, a) =>
        {
            var ok = AutomationSession.TryEdit(f =>
            {
                var c = f.Find(oldId) ?? throw new InvalidOperationException("the cell is gone");
                var newId = tbId.Text.Trim();
                if (newId.Length == 0) throw new InvalidOperationException("The id is empty.");
                var arrows = rows.Select(r => r.ToArrow()).ToList();

                if (!newId.Equals(oldId, StringComparison.Ordinal))
                {
                    foreach (var other in f.Cells)
                        foreach (var ar in other.Out)
                            if (ar.To.Equals(oldId, StringComparison.OrdinalIgnoreCase)) ar.To = newId;
                    c.Id = newId;
                }
                c.Title = tbTitle.Text.Trim();
                c.Note = tbNote.Text.Trim();

                c.Command = ""; c.HttpUrl = ""; c.Body = ""; c.Headers.Clear(); c.WaitSeconds = 0; c.Level = "info"; c.HttpMethod = "GET"; c.UploadFile = ""; c.UploadTo = "";
                switch (cbStep.SelectedIndex)
                {
                    case 1:
                        c.Step = StepKind.Run;
                        c.Command = tbCmd.Text.Trim();
                        if (c.Command.Length == 0) throw new InvalidOperationException("The command is empty.");
                        break;
                    case 2:
                        c.Step = StepKind.Http;
                        c.HttpMethod = cbMethod.SelectedItem as string ?? "GET";
                        c.HttpUrl = tbUrl.Text.Trim();
                        c.Body = tbBody.Text.Trim();
                        foreach (var line in tbHeaders.Text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var i = line.IndexOf(':');
                            if (i <= 0) throw new InvalidOperationException($"The header \"{line}\" must look like  Name: value");
                            c.Headers.Add(new KeyValuePair<string, string>(line[..i].Trim(), line[(i + 1)..].Trim()));
                        }
                        break;
                    case 3:
                        c.Step = StepKind.Notify;
                        c.Command = tbText.Text.Trim();
                        if (c.Command.Length == 0) throw new InvalidOperationException("The text of the notification is empty.");
                        c.Level = cbLevel.SelectedItem as string ?? "info";
                        break;
                    case 4:
                        c.Step = StepKind.Wait;
                        c.WaitSeconds = Num(nbWait);
                        break;
                    case 5:
                        c.Step = StepKind.Status;
                        break;
                    case 6:
                    {
                        c.Step = StepKind.Upload;
                        c.UploadFile = tbUpFile.Text.Trim();
                        if (c.UploadFile.Length == 0) throw new InvalidOperationException("Choose the file to upload.");
                        var where = tbUpPath.Text.Trim();
                        if (where.Length == 0) throw new InvalidOperationException("Write the Dropbox path or the URL.");
                        c.UploadTo = (cbUpTo.SelectedIndex switch { 1 => "post ", 2 => "put ", _ => "dropbox " }) + where;
                        if (cbUpTo.SelectedIndex != 0)
                            foreach (var line in tbUpHeaders.Text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                var i = line.IndexOf(':');
                                if (i <= 0) throw new InvalidOperationException($"The header \"{line}\" must look like  Name: value");
                                c.Headers.Add(new KeyValuePair<string, string>(line[..i].Trim(), line[(i + 1)..].Trim()));
                            }
                        break;
                    }
                    default:
                        c.Step = StepKind.None;
                        break;
                }
                c.TimeoutSeconds = cbStep.SelectedIndex is 1 or 2 or 6 ? (int)Math.Round(Num(nbTimeout)) : 0;
                c.Join = cbJoin.SelectedIndex switch { 1 => JoinMode.All, 2 => JoinMode.First, _ => JoinMode.Settle };
                c.Out.Clear();
                c.Out.AddRange(arrows);
                if (!newId.Equals(oldId, StringComparison.Ordinal) && _selected != null) _selected = newId;
            }, out var message);

            if (!ok)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                a.Cancel = true;
            }
        };

        dlg.SecondaryButtonClick += (_, a) =>
        {
            var ok = AutomationSession.TryEdit(f =>
            {
                f.Cells.RemoveAll(x => x.Id.Equals(oldId, StringComparison.OrdinalIgnoreCase));
                foreach (var other in f.Cells) other.Out.RemoveAll(ar => ar.To.Equals(oldId, StringComparison.OrdinalIgnoreCase));
            }, out var message);
            if (!ok)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                a.Cancel = true;
            }
            else if (_selected != null && _selected.Equals(oldId, StringComparison.OrdinalIgnoreCase)) _selected = null;
        };

        dlg.Opened += (_, _) => CursorHelper.ApplyHandCursorToButtons(content);
        await ShowAsync(dlg);
    }

    private async Task OpenArrowEditorAsync(string fromId, int index)
    {
        if (_dialogOpen) return;
        if (AutomationSession.Running) { Say("Stop the run to change an arrow.", InfoBarSeverity.Warning); return; }
        var cell = AutomationSession.Doc.Find(fromId);
        if (cell == null || index < 0 || index >= cell.Out.Count) return;
        var arrow = cell.Out[index];
        var targets = AutomationSession.Doc.Cells.Select(c => c.Id).Where(x => !x.Equals(cell.Id, StringComparison.OrdinalIgnoreCase)).ToList();

        var row = new ArrowRow(targets, arrow, null);
        var error = new TextBlock { Foreground = Solid(Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel
        {
            Spacing = 10, MinWidth = 600,
            Children =
            {
                Note("On success = the cell ended with exit code 0. On failure = any other exit code. Always = either way. " +
                     "Add your own conditions and the arrow fires only when ALL of them are true: the exit code is a number you choose, the output contains a text, " +
                     "and/or a command exits 0 (it can read ASD_EXIT_CODE and ASD_OUTPUT). The text and color are only for how the arrow looks."),
                row.Root, error,
            },
        };

        var dlg = MakeDialog($"Arrow: {cell.Display} → {arrow.To}");
        dlg.Content = Scrollable(content);
        dlg.PrimaryButtonText = "Apply";
        dlg.SecondaryButtonText = "Delete arrow";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;

        dlg.PrimaryButtonClick += (_, a) =>
        {
            var ok = AutomationSession.TryEdit(f =>
            {
                var c = f.Find(fromId) ?? throw new InvalidOperationException("the cell is gone");
                c.Out[index] = row.ToArrow();
            }, out var message);
            if (!ok) { error.Text = message; error.Visibility = Visibility.Visible; a.Cancel = true; }
        };
        dlg.SecondaryButtonClick += (_, a) =>
        {
            var ok = AutomationSession.TryEdit(f =>
            {
                var c = f.Find(fromId) ?? throw new InvalidOperationException("the cell is gone");
                c.Out.RemoveAt(index);
            }, out var message);
            if (!ok) { error.Text = message; error.Visibility = Visibility.Visible; a.Cancel = true; }
        };
        await ShowAsync(dlg);
    }

    private async Task OpenTextAsync()
    {
        if (_dialogOpen) return;
        if (AutomationSession.Running) { Say("Stop the run to edit the text.", InfoBarSeverity.Warning); return; }

        var box = Field("", AutomationSession.CurrentText(), multi: true, height: Math.Max(160, Math.Min(420, (XamlRoot?.Size.Height ?? 800) - 330)));
        box.MinWidth = 720;
        box.AcceptsReturn = true;
        var error = new TextBlock { Foreground = Solid(Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        if (AutomationSession.Invalid)
        {
            error.Text = string.Join("\n", AutomationSession.Errors);
            error.Visibility = Visibility.Visible;
        }

        var dlg = MakeDialog("Automation text");
        dlg.Content = Scrollable(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Note("This is the .automation file itself. Cards and arrows are made from it when you click Apply. Comments (#) are not kept when you edit on the canvas."),
                box, error,
            },
        });
        dlg.PrimaryButtonText = "Apply";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.PrimaryButtonClick += (_, a) =>
        {
            if (!AutomationSession.SetText(box.Text, out var errors))
            {
                error.Text = string.Join("\n", errors.Take(8));
                error.Visibility = Visibility.Visible;
                a.Cancel = true;
            }
        };
        await ShowAsync(dlg);
    }

    private async Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        if (_dialogOpen) return false;
        var dlg = MakeDialog(title);
        dlg.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        dlg.PrimaryButtonText = yes;
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Close;
        return await ShowAsync(dlg) == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!AutomationSession.Dirty || (AutomationSession.Doc.Cells.Count == 0 && !AutomationSession.Invalid)) return true;
        return await ConfirmAsync("Discard changes?", "The current automation has changes that are not saved to a file.", "Discard");
    }

    /// <summary>A file that did not come from ASD is shown once before it runs.</summary>
    private async Task<bool> ConfirmTrustAsync()
    {
        if (_dialogOpen) return false;
        var doc = AutomationSession.Doc;
        var commands = doc.Cells.Where(c => c.Step == StepKind.Run).ToList();
        var requests = doc.Cells.Count(c => c.Step == StepKind.Http);
        var lines = commands.Take(6).Select(c => "> " + Flat(c.Command)).ToList();
        if (commands.Count > 6) lines.Add($"… and {commands.Count - 6} more");

        var panel = new StackPanel { Spacing = 8, MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "This automation was not made in ASD (or it was changed outside ASD). When it runs it can start programs, send web requests and use keys from your Keys Store, " +
                   $"all under your Windows account. It has {commands.Count} command(s) and {requests} web request(s). Only run it if you trust where it came from.",
        });
        if (lines.Count > 0)
            panel.Children.Add(new TextBox
            {
                Text = string.Join("\r\n", lines), IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.NoWrap, MaxHeight = 160,
            });

        var dlg = MakeDialog("Run this automation?");
        dlg.Content = panel;
        dlg.PrimaryButtonText = "Run";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Close;
        if (await ShowAsync(dlg) != ContentDialogResult.Primary) return false;
        AutomationSession.Trust();
        return true;
    }

    // ───────── commands ─────────

    private async Task NewAsync()
    {
        try
        {
            if (!await ConfirmDiscardAsync()) return;
            AutomationSession.NewDocument();
            _selected = null;
            _log.Text = "";
            Say("New automation", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
    }

    private async Task OpenFileAsync()
    {
        try
        {
            if (!await ConfirmDiscardAsync()) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".automation");
            picker.FileTypeFilter.Add(".txt");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var text = await System.IO.File.ReadAllTextAsync(file.Path);
            _log.Text = "";
            _selected = null;
            if (AutomationSession.Open(file.Path, text)) Say("Opened. Nothing runs until you click Start.", InfoBarSeverity.Informational);
            else await OpenTextAsync();
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
    }

    private async Task SaveAsync(bool saveAs)
    {
        try
        {
            if (AutomationSession.Invalid) { Say("The text has errors. Fix them with the Text button first.", InfoBarSeverity.Warning); return; }
            var path = AutomationSession.FilePath;
            if (saveAs || path == null)
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedFileName = AutomationSession.Doc.Name.Length > 0 ? AutomationSession.Doc.Name : "automation",
                };
                picker.FileTypeChoices.Add("Automation", new List<string> { ".automation" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                path = file.Path;
            }
            var error = AutomationSession.Save(path);
            if (error == null) Say("Saved " + path, InfoBarSeverity.Success);
            else Say("Could not save: " + error, InfoBarSeverity.Error);
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
    }

    private async Task AddCellAsync()
    {
        try
        {
            var doc = AutomationSession.Doc;
            var n = doc.Cells.Count + 1;
            var id = "cell" + n;
            while (doc.Find(id) != null) id = "cell" + (++n);

            // first free place on a grid (300 x 150), so a new card never lands on top of another one
            var x = 40;
            var y = 40;
            for (var slot = 0; slot < 400; slot++)
            {
                x = 40 + (slot % 4) * 300;
                y = 40 + (slot / 4) * 150;
                if (!doc.Cells.Any(o => Math.Abs(o.X - x) < 260 && Math.Abs(o.Y - y) < 130)) break;
            }
            var ok = AutomationSession.TryEdit(f => f.Cells.Add(new AutoCell
            {
                Id = id, Title = "New cell", Step = StepKind.Run, Command = "echo hello", X = x, Y = y,
            }), out var error);
            if (!ok) { Say(error, InfoBarSeverity.Warning); return; }
            Select(id);
            await OpenInspectorAsync(id);
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
    }

    private async Task StartAsync(string? startAt, bool single, bool showLog = true)
    {
        try
        {
            if (AutomationSession.Running) { Say("An automation is already running.", InfoBarSeverity.Informational); return; }
            if (AutomationSession.Invalid) { Say("The text has errors. Fix them with the Text button first.", InfoBarSeverity.Warning); return; }
            if (AutomationSession.Doc.Cells.Count == 0) { Say("Add a cell first.", InfoBarSeverity.Informational); return; }
            if (startAt != null && AutomationSession.Doc.Find(startAt) == null) return;
            if (_dialogOpen) return;
            if (!AutomationSession.Trusted && !await ConfirmTrustAsync()) return;

            _log.Text = "";
            if (showLog) ShowLog(true);
            _info.IsOpen = false;
            AutomationSession.Start(startAt, single);
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
    }
}
