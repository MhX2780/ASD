using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

/// <summary>Start page: a shortcut to the Toolbox and the tools the user starred.</summary>
public sealed class HomePage : ToolPage
{
    public HomePage() : base("home", "Home",
        "Welcome to ASD. Open the Toolbox to see every tool, or press Ctrl+K to jump to any of them.")
    {
        Body.Children.Add(Btn("Open Toolbox", () => (App.MainWindow as MainWindow)?.OpenTool("more"), accent: true));

        var favs = ToolRegistry.Favorites();
        var tools = ToolRegistry.All.Where(t => t.Group == "more" && favs.Contains(t.Tag)).ToList();

        if (tools.Count == 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "No favorites yet. Star a tool in the Toolbox and it will show up here.",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        Body.Children.Add(Label("Favorites"));
        Body.Children.Add(new ItemsRepeater
        {
            ItemsSource = tools.Select(Card).ToList(),
            Layout = new UniformGridLayout
            {
                MinItemWidth = 280,
                MinItemHeight = 118,
                MinRowSpacing = 6,
                MinColumnSpacing = 6,
                ItemsStretch = UniformGridLayoutItemsStretch.Fill,
            },
        });
    }

    private static UIElement Card(ToolInfo t)
    {
        var content = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new FontIcon { Glyph = t.Glyph, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) },
                new TextBlock { Text = t.Title, FontWeight = FontWeights.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = t.Description, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        var button = new Button
        {
            Content = content,
            Height = 118,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14),
            Margin = new Thickness(4),
        };
        button.Click += (_, _) => (App.MainWindow as MainWindow)?.OpenTool(t.Tag);
        return button;
    }
}
