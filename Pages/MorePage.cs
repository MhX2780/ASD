using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

public sealed class MorePage : ToolPage
{
    private readonly StackPanel _host = new() { Spacing = 10 };

    public MorePage() : base("more", "Toolbox",
        "All your tools in one place. Star a tool to pin it to the top and to Home. Tip: press Ctrl+K to jump to any tool.")
    {
        Body.Children.Add(_host);
        Rebuild();
    }

    private string? _category;   // null = the Toolbox itself (category cards); a name = the tools of that category
    internal bool InCategory => _category != null;

    /// <summary>"cat:Web" opens the Toolbox on that category; "cat:" is the Toolbox itself (MainWindow passes this as the page parameter).</summary>
    protected override void OnClipboardInput(string text)
    {
        if (!text.StartsWith("cat:", StringComparison.Ordinal)) return;
        var name = text[4..];
        _category = name.Length == 0 ? null : name;
        Rebuild();
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        var favs = ToolRegistry.Favorites();
        var tools = ToolRegistry.All.Where(t => t.Group == "more").ToList();

        if (_category != null)
        {
            // one category: its name above (this page is already the Toolbox), then its tools as cards
            SetBreadcrumb(new[] { _category }, _ => { });
            _host.Children.Add(MakeGrid(tools.Where(t => t.Category == _category).ToList(), favs));
        }
        else
        {
            SetBreadcrumb(Array.Empty<string>(), _ => { });
            var favTools = tools.Where(t => favs.Contains(t.Tag)).ToList();
            if (favTools.Count > 0)
            {
                _host.Children.Add(Label("Favorites"));
                _host.Children.Add(MakeGrid(favTools, favs));
            }

            _host.Children.Add(Label("Categories"));
            var cards = ToolRegistry.CategoryOrder
                .Select(name => (Name: name, Tools: tools.Where(t => t.Category == name).ToList()))
                .Where(c => c.Tools.Count > 0)
                .Select(c => CategoryCard(c.Name, c.Tools))
                .ToList();
            _host.Children.Add(new ItemsRepeater
            {
                ItemsSource = cards,
                Layout = new UniformGridLayout
                {
                    MinItemWidth = 280,
                    MinItemHeight = 132,
                    MinRowSpacing = 6,
                    MinColumnSpacing = 6,
                    ItemsStretch = UniformGridLayoutItemsStretch.Fill,
                },
            });
        }

        if (IsLoaded) CursorHelper.ApplyHandCursorToButtons(this);
    }

    /// <summary>A card for a whole category (for example "Images &amp; Media"): its icon, name, how many tools, and the first few names.</summary>
    private static UIElement CategoryCard(string name, List<ToolInfo> inCategory)
    {
        var names = string.Join(" · ", inCategory.Take(4).Select(t => t.Title)) + (inCategory.Count > 4 ? " …" : "");
        var content = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new FontIcon { Glyph = ToolRegistry.CategoryGlyph(name), FontSize = 26, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = name, FontWeight = FontWeights.Bold, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = inCategory.Count == 1 ? "1 tool" : $"{inCategory.Count} tools", Opacity = 0.7, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = names, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        var card = new Button
        {
            Content = content,
            Height = 132,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(16),
            Margin = new Thickness(4),
        };
        AutomationProperties.SetName(card, name);
        card.Click += (_, _) => (App.MainWindow as MainWindow)?.OpenTool("more", "cat:" + name);
        return card;
    }

    private UIElement MakeGrid(List<ToolInfo> tools, HashSet<string> favs)
        => new ItemsRepeater
        {
            ItemsSource = tools.Select(t => Card(t, favs.Contains(t.Tag))).ToList(),
            Layout = new UniformGridLayout
            {
                MinItemWidth = 280,
                MinItemHeight = 118,
                MinRowSpacing = 6,
                MinColumnSpacing = 6,
                ItemsStretch = UniformGridLayoutItemsStretch.Fill,
            },
        };

    private UIElement Card(ToolInfo t, bool fav)
    {
        var content = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 26, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new FontIcon { Glyph = t.Glyph, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = t.Title, FontWeight = FontWeights.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = t.Description, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        var main = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14),
            Margin = new Thickness(4),
        };
        AutomationProperties.SetName(main, t.Title);
        main.Click += (_, _) => (App.MainWindow as MainWindow)?.OpenTool(t.Tag);

        var star = new Button
        {
            Content = new FontIcon { Glyph = fav ? "\uE735" : "\uE734", FontSize = 14 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 12, 0),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(star, fav ? "Remove from favorites" : "Add to favorites");
        star.Click += (_, _) => { ToolRegistry.ToggleFavorite(t.Tag); Rebuild(); };

        var g = new Grid { Height = 118 };
        g.Children.Add(main);
        g.Children.Add(star);
        return g;
    }
}
