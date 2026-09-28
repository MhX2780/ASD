using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed partial class EncodePage : Page
{
    public EncodePage()
    {
        this.InitializeComponent();
    }

    private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => CursorHelper.ApplyHandCursorToButtons(this);

    private string SelectedMethod =>
        (MethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Base64";

    private void OnEncodeClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var input = InputBox.Text ?? string.Empty;
            OutputBox.Text = SelectedMethod switch
            {
                "Base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
                "URL" => Uri.EscapeDataString(input),
                "Hex" => Convert.ToHexString(Encoding.UTF8.GetBytes(input)),
                "ROT13" => Rot13(input),
                _ => input,
            };
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void OnDecodeClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var input = InputBox.Text ?? string.Empty;
            OutputBox.Text = SelectedMethod switch
            {
                "Base64" => Encoding.UTF8.GetString(Convert.FromBase64String(input)),
                "URL" => Uri.UnescapeDataString(input),
                "Hex" => Encoding.UTF8.GetString(Convert.FromHexString(input)),
                "ROT13" => Rot13(input),
                _ => input,
            };
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private static string Rot13(string input)
    {
        var chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c >= 'a' && c <= 'z')
                chars[i] = (char)(((c - 'a' + 13) % 26) + 'a');
            else if (c >= 'A' && c <= 'Z')
                chars[i] = (char)(((c - 'A' + 13) % 26) + 'A');
        }
        return new string(chars);
    }

    private void OnSwapClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        (InputBox.Text, OutputBox.Text) = (OutputBox.Text, InputBox.Text);
    }

    private void OnClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        OutputBox.Text = string.Empty;
        StatusText.Text = string.Empty;
    }

    private void OnCopyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(OutputBox.Text ?? string.Empty);
        Clipboard.SetContent(package);
        StatusText.Text = "Copied!";
    }
}
