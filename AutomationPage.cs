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

    private sealed class CardView
    {
        public string Id = "";
        public Border Root = null!;
        public Button Handle = null!;
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
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 190,
        FontFamily = new FontFamily("Consolas"), FontSize = 12,
    };
    private readonly Border _logPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _empty;

    private Button _btnNew = null!, _btnOpen = null!, _btnSave = null!, _btnSaveAs = null!, _btnUndo = null!, _btnAdd = null!,
                   _btnStart = null!, _btnStop = null!, _btnSettings = null!, _btnText = null!, _btnLog = null!;

    private string? _selected;
    private DragInfo? _drag;
    private PanInfo? _pan;
    private string? _linkFrom;
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

        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Menu, () => { if (_selected != null) _ = OpenInspectorAsync(_selected); });
        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Control, () => { if (_selected != null) _ = StartAsync(_selected, true); });
        AddAccelerator(Windows.System.VirtualKey.Enter, Windows.System.VirtualKeyModifiers.Shift, () => { if (_selected != null) _ = StartAsync(_selected, false); });
        AddAccelerator(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control, () => _ = SaveAsync(false));

        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
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
        _btnNew = ActionBtn("", "New", () => _ = NewAsync());
        _btnOpen = ActionBtn("", "Open", () => _ = OpenFileAsync());
        _btnSave = ActionBtn("", "Save", () => _ = SaveAsync(false));
        _btnSaveAs = ActionBtn("", "Save as", () => _ = SaveAsync(true));
        _btnUndo = ActionBtn("", "Undo", () => AutomationSession.UndoLast());
        _btnAdd = ActionBtn("", "Add cell", () => _ = AddCellAsync());
        _btnSettings = ActionBtn("", "Cell settings", () => { if (_selected != null) _ = OpenInspectorAsync(_selected); });
        _btnText = ActionBtn("", "Text", () => _ = OpenTextAsync());
        _btnStart = ActionBtn("", "Start", () => _ = StartAsync(null, false), accent: true);
        _btnStop = ActionBtn("", "Stop", () => AutomationSession.Stop());
        _btnLog = ActionBtn("", "Log", () => ShowLog(_logPanel.Visibility != Visibility.Visible));
        ToolTipService.SetToolTip(_btnSettings, "Settings of the selected cell (Alt+Enter)");
        ToolTipService.SetToolTip(_btnStart, "Run the whole automation");
        ToolTipService.SetToolTip(_btnText, "Edit the .automation text directly");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new UIElement[] { _btnNew, _btnOpen, _btnSave, _btnSaveAs, _btnUndo, Separator(), _btnAdd, _btnSettings, _btnText, Separator(), _btnStart, _btnStop, Separator(), _btnLog })
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
        _scroll.ZoomMode = ZoomMode.Disabled;
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
        var areaBox = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.4 },
            Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.06 },
            Child = area,
        };

        var hint = new TextBlock
        {
            Text = "Left mouse: move a card · drag the dot on its right edge onto another card to connect them · click an arrow's label to change it · " +
                   "right mouse (or the empty canvas): move the view · Alt+Enter: cell settings · Ctrl+Enter: run this cell · Shift+Enter: run from this cell",
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
        Grid.SetRow(_logPanel, 4);
        root.Children.Add(header);
        root.Children.Add(_info);
        root.Children.Add(areaBox);
        root.Children.Add(hint);
        root.Children.Add(_logPanel);

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
        _canvas.Width = Math.Max(w + 400, Math.Max(_scroll.ViewportWidth, 600));
        _canvas.Height = Math.Max(h + 300, Math.Max(_scroll.ViewportHeight, 400));
    }

    private static string StepSummary(AutoCell c) => c.Step switch
    {
        StepKind.Run => "> " + Flat(c.Command),
        StepKind.Http => c.HttpMethod + " " + Flat(c.HttpUrl),
        StepKind.Notify => "notify: " + Flat(c.Command),
        StepKind.Wait => $"wait {c.WaitSeconds:0.##} s",
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
        v.Root.PointerPressed += Card_Pressed;
        v.Root.PointerMoved += Card_Moved;
        v.Root.PointerReleased += Card_Released;
        v.Root.PointerCaptureLost += (_, _) => _drag = null;

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
        Canvas.SetLeft(v.Handle, x + CardW - 7);
        Canvas.SetTop(v.Handle, y + CardH / 2 - 7);
    }

    private void RefreshRun()
    {
        var doc = AutomationSession.Doc;
        foreach (var c in doc.Cells)
            if (_cards.TryGetValue(c.Id, out var v)) ApplyState(v, c);
        DrawArrows();
        RefreshToolbar();
        RefreshFileLabel();
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
        v.Root.BorderThickness = new Thickness(selected ? 3 : 2);
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
        _btnStop.IsEnabled = s;
        _btnUndo.IsEnabled = AutomationSession.CanUndo && !s;
        _btnNew.IsEnabled = !s;
        _btnOpen.IsEnabled = !s;
        _btnSave.IsEnabled = !AutomationSession.Invalid;
        _btnSaveAs.IsEnabled = !AutomationSession.Invalid;
        _btnAdd.IsEnabled = !s && !AutomationSession.Invalid;
        _btnText.IsEnabled = !s;
        _btnSettings.IsEnabled = _selected != null && !s;
    }

    // ───────── arrows ─────────

    private static Windows.UI.Color ArrowColor(AutoArrow a)
        => a.When != null ? Orange : a.Kind switch { ArrowKind.Success => Green, ArrowKind.Failure => Red, _ => Grey };

    private static string BadgeText(AutoArrow a)
    {
        var word = a.Kind switch { ArrowKind.Success => "success", ArrowKind.Failure => "failure", _ => "always" };
        if (a.When == null) return word;
        var cond = Flat(a.When);
        if (cond.Length > 26) cond = cond[..26] + "…";
        return a.Kind == ArrowKind.Always ? "when " + cond : word + " · when " + cond;
    }

    private void DrawArrows(bool applyCursor = true)
    {
        foreach (var el in _arrowElems) _canvas.Children.Remove(el);
        _arrowElems.Clear();

        foreach (var c in AutomationSession.Doc.Cells)
        {
            if (!_cards.TryGetValue(c.Id, out var from)) continue;
            var groups = c.Out.Select((arrow, index) => (Arrow: arrow, Index: index)).GroupBy(t => t.Arrow.To.ToLowerInvariant());
            foreach (var g in groups)
            {
                var list = g.ToList();
                for (var k = 0; k < list.Count; k++) DrawArrow(c, from, list[k].Arrow, list[k].Index, k, list.Count);
            }
        }
        if (applyCursor) CursorHelper.ApplyHandCursorToButtons(_canvas);
    }

    private void DrawArrow(AutoCell c, CardView from, AutoArrow a, int index, int k, int n)
    {
        if (!_cards.TryGetValue(a.To, out var to)) return;

        var x0 = Canvas.GetLeft(from.Root) + CardW;
        var y0 = Canvas.GetTop(from.Root) + CardH / 2;
        var x1 = Canvas.GetLeft(to.Root);
        var y1 = Canvas.GetTop(to.Root) + CardH / 2;
        var off = (k - (n - 1) / 2.0) * 34;
        var dx = Math.Max(60, Math.Abs(x1 - x0) / 2);
        var p0 = new Point(x0, y0);
        var p1 = new Point(x0 + dx, y0 + off);
        var p2 = new Point(x1 - dx, y1 + off);
        var p3 = new Point(x1, y1);

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

        var mid = new Point((p0.X + 3 * p1.X + 3 * p2.X + p3.X) / 8, (p0.Y + 3 * p1.Y + 3 * p2.Y + p3.Y) / 8);
        var fill = Solid(color);
        var none = Solid(Colors.Transparent);
        var badge = new Button
        {
            Content = new TextBlock { Text = BadgeText(a), FontSize = 11, Foreground = Solid(Colors.White), MaxLines = 1 },
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
        Canvas.SetLeft(badge, mid.X - badge.DesiredSize.Width / 2);
        Canvas.SetTop(badge, mid.Y - badge.DesiredSize.Height / 2);

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

    private void Card_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var card = (Border)sender;
        var pt = e.GetCurrentPoint(_canvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var id = (string)card.Tag;
        _drag = new DragInfo { Id = id, Start = pt.Position, X0 = Canvas.GetLeft(card), Y0 = Canvas.GetTop(card) };
        card.CapturePointer(e.Pointer);
        Select(id);
        e.Handled = true;
    }

    private void Card_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == null || !_cards.TryGetValue(_drag.Id, out var v) || !ReferenceEquals(sender, v.Root)) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        var dx = p.X - _drag.Start.X;
        var dy = p.Y - _drag.Start.Y;
        if (!_drag.Moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _drag.Moved = true;
        Place(v, Math.Max(0, Math.Round((_drag.X0 + dx) / 10) * 10), Math.Max(0, Math.Round((_drag.Y0 + dy) / 10) * 10));
        DrawArrows(applyCursor: false);
    }

    private void Card_Released(object sender, PointerRoutedEventArgs e)
    {
        var d = _drag;
        _drag = null;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (d == null) return;

        if (d.Moved)
        {
            if (_cards.TryGetValue(d.Id, out var v))
                AutomationSession.MoveCell(d.Id, (int)Canvas.GetLeft(v.Root), (int)Canvas.GetTop(v.Root));
            UpdateCanvasSize();
            DrawArrows();
            RefreshFileLabel();
        }
        else _ = OpenInspectorAsync(d.Id);
        e.Handled = true;
    }

    private void Handle_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var h = (FrameworkElement)sender;
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (AutomationSession.Running) { Say("Stop the run before changing arrows.", InfoBarSeverity.Warning); return; }
        _linkFrom = (string)h.Tag;
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
        var x0 = Canvas.GetLeft(v.Root) + CardW;
        var y0 = Canvas.GetTop(v.Root) + CardH / 2;
        var dx = Math.Max(40, Math.Abs(p.X - x0) / 2);
        var figure = new PathFigure { StartPoint = new Point(x0, y0), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = new Point(x0 + dx, y0), Point2 = new Point(p.X - dx, p.Y), Point3 = p });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _linkPath.Data = geometry;
    }

    private void Handle_Released(object sender, PointerRoutedEventArgs e)
    {
        var from = _linkFrom;
        EndLink();
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (from == null) return;
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

    private void ConnectCells(string from, string to)
    {
        var chosen = ArrowKind.Success;
        var ok = AutomationSession.TryEdit(f =>
        {
            var c = f.Find(from) ?? throw new InvalidOperationException("the cell is gone");
            var target = f.Find(to) ?? throw new InvalidOperationException("the target cell is gone");
            chosen = !c.Out.Any(a => a.Kind == ArrowKind.Success) ? ArrowKind.Success
                   : !c.Out.Any(a => a.Kind == ArrowKind.Failure) ? ArrowKind.Failure
                   : ArrowKind.Always;
            if (c.Out.Any(a => a.Kind == chosen && a.When == null && a.To.Equals(target.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That arrow already exists.");
            c.Out.Add(new AutoArrow { Kind = chosen, To = target.Id });
        }, out var error);

        if (ok)
        {
            var word = chosen switch { ArrowKind.Success => "success (green)", ArrowKind.Failure => "failure (red)", _ => "always (grey)" };
            Say($"Arrow added: {word}. Click its label to change when it fires.", InfoBarSeverity.Informational);
        }
        else Say(error, InfoBarSeverity.Warning);
    }

    private void Canvas_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_scroll);
        var onEmpty = pt.Properties.IsLeftButtonPressed && ReferenceEquals(e.OriginalSource, _canvas);
        if (!pt.Properties.IsRightButtonPressed && !onEmpty) return;
        _pan = new PanInfo { Start = pt.Position, H = _scroll.HorizontalOffset, V = _scroll.VerticalOffset };
        _canvas.CapturePointer(e.Pointer);
        if (onEmpty) Select(null);
    }

    private void Canvas_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_pan == null) return;
        var p = e.GetCurrentPoint(_scroll).Position;
        _scroll.ChangeView(_pan.H - (p.X - _pan.Start.X), _pan.V - (p.Y - _pan.Start.Y), null, true);
    }

    private void Canvas_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_pan == null) return;
        _pan = null;
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    private void Canvas_CaptureLost(object sender, PointerRoutedEventArgs e) => _pan = null;

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

    /// <summary>One arrow in a list: type, optional "only if this command exits 0", target, delete.</summary>
    private sealed class ArrowRow
    {
        public readonly Grid Root = new() { ColumnSpacing = 8 };
        public readonly ComboBox Kind = new() { MinWidth = 190 };
        public readonly TextBox When = new() { PlaceholderText = "only if this command exits 0 (optional)", IsSpellCheckEnabled = false, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Stretch };
        public readonly ComboBox Target = new() { MinWidth = 150 };
        public readonly Button Delete = new() { Content = new FontIcon { Glyph = "", FontSize = 14 } };

        public ArrowRow(IEnumerable<string> targets, AutoArrow? a, Action<ArrowRow>? onDelete)
        {
            Kind.Items.Add("On success (exit 0)");
            Kind.Items.Add("On failure (exit not 0)");
            Kind.Items.Add("Always");
            Kind.SelectedIndex = a == null ? 0 : a.Kind switch { ArrowKind.Success => 0, ArrowKind.Failure => 1, _ => 2 };
            foreach (var t in targets) Target.Items.Add(t);
            if (a != null && Target.Items.Contains(a.To)) Target.SelectedItem = a.To;
            else if (Target.Items.Count > 0) Target.SelectedIndex = 0;
            When.Text = a?.When ?? "";
            ToolTipService.SetToolTip(Delete, "Delete this arrow");
            AutomationProperties.SetName(Delete, "Delete this arrow");

            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(When, 1);
            Grid.SetColumn(Target, 2);
            Grid.SetColumn(Delete, 3);
            Root.Children.Add(Kind);
            Root.Children.Add(When);
            Root.Children.Add(Target);
            if (onDelete != null) { Root.Children.Add(Delete); Delete.Click += (_, _) => onDelete(this); }
        }

        public AutoArrow ToArrow()
        {
            var to = Target.SelectedItem as string ?? "";
            if (to.Length == 0) throw new InvalidOperationException("An arrow has no target cell.");
            var when = When.Text.Trim();
            return new AutoArrow
            {
                Kind = Kind.SelectedIndex switch { 0 => ArrowKind.Success, 1 => ArrowKind.Failure, _ => ArrowKind.Always },
                When = when.Length > 0 ? when : null,
                To = to,
            };
        }
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
        foreach (var s in new[] { "Nothing (only passes through, e.g. a Finish marker)", "Run a command", "HTTP request", "Notification", "Wait" }) cbStep.Items.Add(s);
        cbStep.SelectedIndex = cell.Step switch { StepKind.Run => 1, StepKind.Http => 2, StepKind.Notify => 3, StepKind.Wait => 4, _ => 0 };

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
            nbTimeout.Visibility = i is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
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
                tbId, tbTitle, tbNote, cbStep, pRun, pHttp, pNotify, pWait, nbTimeout, cbJoin,
                new TextBlock { Text = "Arrows out of this cell", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                Note("Green = on success · red = on failure · grey = always. Fill the box to add a condition: the arrow then fires only if that command exits 0 (shown in orange)."),
                rowsPanel, addArrow, error,
            },
        };

        var dlg = MakeDialog("Cell: " + cell.Display);
        dlg.Content = content;
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

                c.Command = ""; c.HttpUrl = ""; c.Body = ""; c.Headers.Clear(); c.WaitSeconds = 0; c.Level = "info"; c.HttpMethod = "GET";
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
                    default:
                        c.Step = StepKind.None;
                        break;
                }
                c.TimeoutSeconds = cbStep.SelectedIndex is 1 or 2 ? (int)Math.Round(Num(nbTimeout)) : 0;
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
                     "If you type a command, the arrow fires only when the cell's result matches AND that command exits 0. " +
                     "The command can read the cell's result from the environment variables ASD_EXIT_CODE and ASD_OUTPUT."),
                row.Root, error,
            },
        };

        var dlg = MakeDialog($"Arrow: {cell.Display} → {arrow.To}");
        dlg.Content = content;
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

        var box = Field("", AutomationSession.CurrentText(), multi: true, height: 420);
        box.MinWidth = 720;
        box.AcceptsReturn = true;
        var error = new TextBlock { Foreground = Solid(Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        if (AutomationSession.Invalid)
        {
            error.Text = string.Join("\n", AutomationSession.Errors);
            error.Visibility = Visibility.Visible;
        }

        var dlg = MakeDialog("Automation text");
        dlg.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Note("This is the .automation file itself. Cards and arrows are made from it when you click Apply. Comments (#) are not kept when you edit on the canvas."),
                box, error,
            },
        };
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

    private async Task StartAsync(string? startAt, bool single)
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
            ShowLog(true);
            _info.IsOpen = false;
            AutomationSession.Start(startAt, single);
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); }
    }
}
