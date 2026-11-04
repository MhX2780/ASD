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

        // at a glance: the state of the things that live outside a single page
        Body.Children.Add(Label("At a glance"));
        Body.Children.Add(Tiles());

        var recent = ToolRegistry.Recent(6);
        if (recent.Count > 0)
        {
            Body.Children.Add(Label("Recently used"));
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var t in recent)
            {
                var tool = t;
                var chip = new Button
                {
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = tool.Glyph, FontSize = 14 }, new TextBlock { Text = tool.Title } } },
                    Padding = new Thickness(12, 6, 14, 6), CornerRadius = new CornerRadius(16),
                };
                chip.Click += (_, _) => (App.MainWindow as MainWindow)?.OpenTool(tool.Tag);
                chips.Children.Add(chip);
            }
            Body.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Auto, Content = chips });
        }

        // Tools with their own navigation item (e.g. Video Player) get a card on Home too
        var quick = ToolRegistry.All.Where(t => t.Group == "main" && t.Tag != "home").ToList();
        if (quick.Count > 0)
        {
            Body.Children.Add(Label("Quick access"));
            Body.Children.Add(new ItemsRepeater
            {
                ItemsSource = quick.Select(Card).ToList(),
                Layout = new UniformGridLayout
                {
                    MinItemWidth = 280, MinItemHeight = 118, MinRowSpacing = 6, MinColumnSpacing = 6,
                    ItemsStretch = UniformGridLayoutItemsStretch.Fill,
                },
            });
        }

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

    // ───────── "At a glance" ─────────

    private static UIElement Tile(string glyph, string title, out TextBlock value, Action open)
    {
        value = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left };
        var button = new Button
        {
            Content = new StackPanel
            {
                Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 16 }, new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 } } },
                    value,
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(4),
        };
        button.Click += (_, _) => open();
        return button;
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }

    private UIElement Tiles()
    {
        var list = new List<UIElement>();

        list.Add(Tile("\uE898", "Dropbox", out var dropbox, () => (App.MainWindow as MainWindow)?.OpenTool("upload")));
        try { dropbox.Text = DropboxStore.SignedIn ? "Signed in: uploads are ready" : "Not signed in (Upload File, Dropbox menu)"; } catch { dropbox.Text = "Not signed in"; }

        list.Add(Tile("\uE768", "Automation", out var auto, () => (App.MainWindow as MainWindow)?.OpenTool("automation")));
        try
        {
            var log = AppPaths.File("automation.log");
            auto.Text = File.Exists(log) ? "Last run " + Ago(File.GetLastWriteTime(log)) : "No run yet";
        }
        catch { auto.Text = "No run yet"; }

        list.Add(Tile("\uE72E", "Keys Store", out var keys, () => (App.MainWindow as MainWindow)?.OpenTool("keys")));
        try { keys.Text = KeyVault.Exists ? KeyVault.All().Count + (KeyVault.All().Count == 1 ? " key saved" : " keys saved") : "Not created yet"; } catch { keys.Text = "Open to see"; }

        list.Add(Tile("\uEA86", "Extensions", out var ext, () => ExtensionsWindow.ShowWindow()));
        try
        {
            var installed = Directory.Exists(ExtensionStore.InstalledRoot) ? Directory.GetDirectories(ExtensionStore.InstalledRoot).Count(d => !d.EndsWith(".new")) : 0;
            ext.Text = installed == 0 ? "None installed" : installed + " installed";
            var shown = ext;
            _ = Task.Run(async () =>
            {
                var news = await ExtensionStore.QuickCountAsync(false, CancellationToken.None);
                if (news > 0) DispatcherQueue.TryEnqueue(() => shown.Text += $"  ·  {news} new or updated");
            });
        }
        catch { ext.Text = "Open to see"; }

        return new ItemsRepeater
        {
            ItemsSource = list,
            Layout = new UniformGridLayout { MinItemWidth = 230, MinItemHeight = 84, MinRowSpacing = 6, MinColumnSpacing = 6, ItemsStretch = UniformGridLayoutItemsStretch.Fill },
        };
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
