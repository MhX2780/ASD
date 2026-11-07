using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ASD;

/// <summary>
/// Extensions, as a page in the main window (like HTTP Download): the progress of reading the store, then the tabs
/// (Available / New / Updates / Installed), the search box and a card per extension. Clicking a card opens that
/// extension's own small window (details, install, update, remove).
/// </summary>
public sealed class ExtensionsPage : ToolPage
{
    // the list is kept for a few minutes, so coming back to the page does not read the store again
    private static List<ExtInfo>? _cache;
    private static DateTime _cacheTime;
    private static string _tab = "all";          // all | new | updates | installed
    private static string _query = "";

    private readonly CancellationTokenSource _cts = new();
    private List<ExtInfo> _all = new();

    // loading view
    private readonly StackPanel _loading = new() { Spacing = 14, Margin = new Thickness(0, 40, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _ring = new() { IsActive = true, Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FontIcon _errorIcon = new() { Glyph = "", FontSize = 56, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, IsTextSelectionEnabled = true };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Width = 380 };
    private readonly TextBlock _stats = new() { Opacity = 0.7, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _retry = new() { Content = "Try again", HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };

    // list view
    private readonly StackPanel _listView = new() { Spacing = 12, Visibility = Visibility.Collapsed };
    private ItemsRepeater? _cards;
    private TextBlock? _empty;
    private SelectorBar? _tabs;
    private bool _building;

    public ExtensionsPage() : base("extensions", "Extensions", "Add tools to ASD. Each one is a small add-on from the ASD-Extensions store.")
    {
        _loading.Children.Add(_ring);
        _loading.Children.Add(_errorIcon);
        _loading.Children.Add(_title);
        _loading.Children.Add(_detail);
        _loading.Children.Add(_bar);
        _loading.Children.Add(_stats);
        _loading.Children.Add(_retry);
        Body.Children.Add(_loading);
        Body.Children.Add(_listView);

        _retry.Click += async (_, _) => await LoadAsync(true);
        Loaded += async (_, _) => await LoadAsync(false);
        Unloaded += (_, _) => _cts.Cancel();
    }

    // ───────── colors and small pieces ─────────

    private static Windows.UI.Color AccentColor()
    {
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
        catch { return Colors.DodgerBlue; }
    }

    private Brush TextBrush() => new SolidColorBrush(ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White);

    private static TextBlock Text(string text, double size = 14, bool bold = false, double opacity = 1)
        => new()
        {
            Text = text, FontSize = size, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };

    private static Border Badge(string text, bool accent)
        => new()
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
            Background = accent ? new SolidColorBrush(AccentColor()) : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = accent ? new SolidColorBrush(Colors.White) : null },
        };

    /// <summary>The extension's picture: its icon.png, else its Simple Icons logo, else its glyph.</summary>
    private FrameworkElement IconOf(ExtInfo e, double box)
    {
        FrameworkElement? inner = null;
        try
        {
            if (e.IconPath != null) inner = new Image { Width = box * 0.7, Height = box * 0.7, Source = new BitmapImage(new Uri(e.IconPath)) };
            else if (e.SimpleIcon.Length > 0) inner = SimpleIcons.Create(e.SimpleIcon, box * 0.5, TextBrush());
        }
        catch { inner = null; }
        inner ??= new FontIcon { Glyph = e.Glyph.Length > 0 ? e.Glyph : "\uEA86", FontSize = box * 0.5 };
        return new Border
        {
            Width = box, Height = box, CornerRadius = new CornerRadius(box / 5),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Grid { Children = { inner } },
        };
    }

    // ───────── loading ─────────

    private void ShowLoading(string title, string detail)
    {
        _listView.Visibility = Visibility.Collapsed;
        _loading.Visibility = Visibility.Visible;
        _ring.IsActive = true; _ring.Visibility = Visibility.Visible;
        _errorIcon.Visibility = Visibility.Collapsed;
        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = true;
        _bar.Visibility = Visibility.Visible;
        _stats.Visibility = Visibility.Collapsed;
        _retry.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        _listView.Visibility = Visibility.Collapsed;
        _loading.Visibility = Visibility.Visible;
        _ring.IsActive = false; _ring.Visibility = Visibility.Collapsed;
        _errorIcon.Visibility = Visibility.Visible;
        _title.Text = "Could not load the extensions";
        _detail.Text = message;
        _detail.Visibility = Visibility.Visible;
        _bar.Visibility = Visibility.Collapsed;
        _stats.Visibility = Visibility.Collapsed;
        _retry.Visibility = Visibility.Visible;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    private void OnProgress(ExtProgress p)
    {
        _bar.IsIndeterminate = p.Indeterminate;
        if (!p.Indeterminate) _bar.Value = p.Percent;
        switch (p.Stage)
        {
            case "download":
                _title.Text = "Downloading extensions…";
                _stats.Text = p.Total > 0 ? $"{Mb(p.Received)} / {Mb(p.Total)}  ·  {Mb((long)p.BytesPerSecond)}/s  ·  {p.Percent:0}%" : $"{Mb(p.Received)}  ·  {Mb((long)p.BytesPerSecond)}/s";
                _stats.Visibility = Visibility.Visible;
                break;
            case "extract":
                _title.Text = "Unpacking…";
                break;
        }
    }

    private async Task LoadAsync(bool force)
    {
        var ct = _cts.Token;
        if (!force && _cache != null && DateTime.UtcNow - _cacheTime < TimeSpan.FromMinutes(5))
        {
            _all = _cache;
            ShowList();
            return;
        }
        try
        {
            ShowLoading("Looking for extensions…", "Reading the list from GitHub");
            _all = await ExtensionStore.RefreshAsync(new Progress<ExtProgress>(OnProgress), ct);
            _cache = _all;
            _cacheTime = DateTime.UtcNow;
            ShowList();
        }
        catch (OperationCanceledException) { /* the page was left */ }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ───────── the list ─────────

    private bool Matches(ExtInfo e)
    {
        var ok = _tab switch { "new" => e.IsNew && !e.Installed, "updates" => e.HasUpdate, "installed" => e.Installed, _ => true };
        if (!ok) return false;
        var q = _query.Trim().ToLowerInvariant();
        if (q.Length == 0) return true;
        var hay = $"{e.Name} {e.Id} {e.Description} {e.Author}".ToLowerInvariant();
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
    }

    private void ShowList()
    {
        _building = true;
        _loading.Visibility = Visibility.Collapsed;
        _listView.Children.Clear();

        // tabs + search + refresh
        var bar = new Grid { ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tabs = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        void AddTab(string tag, string text, int count)
        {
            var item = new SelectorBarItem { Text = $"{text} ({count})", Tag = tag };
            _tabs.Items.Add(item);
            if (tag == _tab) _tabs.SelectedItem = item;
        }
        AddTab("all", "Available", _all.Count);
        AddTab("new", "New", _all.Count(x => x.IsNew && !x.Installed));
        AddTab("updates", "Updates", _all.Count(x => x.HasUpdate));
        AddTab("installed", "Installed", _all.Count(x => x.Installed));
        if (_tabs.SelectedItem == null) _tabs.SelectedItem = _tabs.Items[0];
        _tabs.SelectionChanged += (s, _) =>
        {
            if (_building) return;
            if (s.SelectedItem is SelectorBarItem { Tag: string t }) { _tab = t; RebuildCards(); }
        };
        var search = new AutoSuggestBox { PlaceholderText = "Search extensions", QueryIcon = new SymbolIcon(Symbol.Find), Text = _query, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search extensions");
        search.TextChanged += (s, a) =>
        {
            if (_building || a.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            _query = s.Text;
            RebuildCards();
        };
        var refresh = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(refresh, "Look for new extensions");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(refresh, "Refresh extensions");
        refresh.Click += async (_, _) => await LoadAsync(true);
        Grid.SetColumn(search, 1);
        Grid.SetColumn(refresh, 2);
        bar.Children.Add(_tabs);
        bar.Children.Add(search);
        bar.Children.Add(refresh);
        _listView.Children.Add(bar);

        if (ExtensionStore.Offline)
            _listView.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = "No connection to GitHub: showing the saved list." });

        // cards
        _cards = new ItemsRepeater
        {
            Layout = new UniformGridLayout { MinItemWidth = 300, MinItemHeight = 124, MinRowSpacing = 12, MinColumnSpacing = 12, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
        };
        _empty = Text("", 14, opacity: 0.7);
        _empty.Margin = new Thickness(0, 24, 0, 0);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _listView.Children.Add(_cards);
        _listView.Children.Add(_empty);

        _listView.Visibility = Visibility.Visible;
        _building = false;
        RebuildCards();
    }

    private void RebuildCards()
    {
        if (_cards == null || _empty == null) return;
        var items = _all.Where(Matches).Select(Card).Cast<UIElement>().ToList();
        _cards.ItemsSource = items;
        _empty.Text = items.Count > 0 ? "" : _all.Count == 0 ? "The store has no extensions yet."
            : _tab == "new" ? "Nothing new." : _tab == "updates" ? "Everything is up to date." : _tab == "installed" ? "No extension is installed." : "No extension matches your search.";
        _empty.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        CursorHelper.ApplyHandCursorToButtons(this);
    }

    private Button Card(ExtInfo e)
    {
        var texts = new StackPanel { Spacing = 3 };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock { Text = e.Name.Length > 0 ? e.Name : e.Id, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 });
        if (e.HasUpdate) nameRow.Children.Add(Badge("Update", true));
        else if (e.Installed) nameRow.Children.Add(Badge("Installed", false));   // (an installed extension is never "New")
        else if (e.IsNew) nameRow.Children.Add(Badge("New", true));
        texts.Children.Add(nameRow);
        var who = string.Join("  ·  ", new[] { e.Author, "v" + (e.Version.Length > 0 ? e.Version : "?") }.Where(s => s.Length > 0));
        texts.Children.Add(new TextBlock { Text = who, FontSize = 12, Opacity = 0.65 });
        texts.Children.Add(new TextBlock { Text = e.Description, FontSize = 13, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2 });

        var inner = new Grid { ColumnSpacing = 12 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.Children.Add(IconOf(e, 46));
        Grid.SetColumn(texts, 1);
        inner.Children.Add(texts);

        var card = new Button
        {
            Content = inner, Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(8),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, "Extension " + e.Name);
        card.Click += (_, _) => ExtensionWindow.ShowFor(e, _all, OnWindowChanged);
        return card;
    }

    /// <summary>The extension window closed: the list may have changed (installed, removed, newer package).</summary>
    private void OnWindowChanged(List<ExtInfo> list)
    {
        _all = list;
        _cache = list;
        _cacheTime = DateTime.UtcNow;
        if (_listView.Visibility == Visibility.Visible) ShowList();
    }
}
