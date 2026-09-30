using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

public sealed class MorePage : ToolPage
{
    private readonly StackPanel _host = new() { Spacing = 10 };

    public MorePage() : base("more", "More tools",
        "Star a tool to pin it to the top of this page. Tip: press Ctrl+K to jump to any tool.")
    {
        Body.Children.Add(_host);
        Rebuild();
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        var favs = ToolRegistry.Favorites();
        var tools = ToolRegistry.All.Where(t => t.Group == "more").ToList();
        var favTools = tools.Where(t => favs.Contains(t.Tag)).ToList();

        if (favTools.Count > 0)
        {
            _host.Children.Add(Label("Favorites"));
            _host.Children.Add(MakeGrid(favTools, favs));
        }
        _host.Children.Add(Label(favTools.Count > 0 ? "All tools" : "Tools"));
        _host.Children.Add(MakeGrid(tools, favs));

        if (IsLoaded) CursorHelper.ApplyHandCursorToButtons(this);
    }

    private UIElement MakeGrid(List<ToolInfo> tools, HashSet<string> favs)
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 310, ItemHeight = 120 };
        foreach (var t in tools) grid.Children.Add(Card(t, favs.Contains(t.Tag)));
        return grid;
    }

    private UIElement Card(ToolInfo t, bool fav)
    {
        var content = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 26, 0),
            Children =
            {
                new FontIcon { Glyph = t.Glyph, FontSize = 20 },
                new TextBlock { Text = t.Title, FontWeight = FontWeights.Bold, FontSize = 16 },
                new TextBlock { Text = t.Description, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap },
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

        var g = new Grid();
        g.Children.Add(main);
        g.Children.Add(star);
        return g;
    }
}
