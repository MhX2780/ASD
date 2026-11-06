using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed partial class TextToolsPage : Page
{
    public TextToolsPage()
    {
        this.InitializeComponent();
    }

    private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => CursorHelper.ApplyHandCursorToButtons(this);

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text ?? string.Empty;
        int chars = text.Length;
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int lines = text.Length == 0 ? 0 : text.Split('\n').Length;
        CountsText.Text = $"{chars} characters  •  {words} words  •  {lines} lines";
    }

    private void OnUpperClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => OutputBox.Text = (InputBox.Text ?? string.Empty).ToUpperInvariant();

    private void OnLowerClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => OutputBox.Text = (InputBox.Text ?? string.Empty).ToLowerInvariant();

    private void OnTitleClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        OutputBox.Text = culture.TextInfo.ToTitleCase((InputBox.Text ?? string.Empty).ToLowerInvariant());
    }

    private void OnTrimLinesClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var lines = (InputBox.Text ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r').Trim());
        OutputBox.Text = string.Join("\n", lines);
    }

    private void OnDedupClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var raw in (InputBox.Text ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (seen.Add(line))
                result.Add(line);
        }
        OutputBox.Text = string.Join("\n", result);
    }

    private void OnFormatJsonClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(InputBox.Text ?? string.Empty);
            var options = new JsonSerializerOptions { WriteIndented = true };
            OutputBox.Text = JsonSerializer.Serialize(doc.RootElement, options);
        }
        catch (JsonException ex)
        {
            OutputBox.Text = $"Invalid JSON: {ex.Message}";
        }
    }

    private void OnCopyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(OutputBox.Text ?? string.Empty);
        Clipboard.SetContent(package);
    }

    private void OnClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        OutputBox.Text = string.Empty;
        CountsText.Text = string.Empty;
    }
}
