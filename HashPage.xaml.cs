using Microsoft.UI.Xaml.Controls;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed partial class HashPage : Page
{
    public HashPage()
    {
        this.InitializeComponent();
    }

    private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => CursorHelper.ApplyHandCursorToButtons(this);

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        var bytes = Encoding.UTF8.GetBytes(InputBox.Text ?? string.Empty);

        Md5Box.Text = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        Sha1Box.Text = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        Sha256Box.Text = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Sha512Box.Text = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
    }

    private void OnClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        Md5Box.Text = string.Empty;
        Sha1Box.Text = string.Empty;
        Sha256Box.Text = string.Empty;
        Sha512Box.Text = string.Empty;
    }

    private void OnCopyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string boxName })
        {
            var text = boxName switch
            {
                "Md5Box" => Md5Box.Text,
                "Sha1Box" => Sha1Box.Text,
                "Sha256Box" => Sha256Box.Text,
                "Sha512Box" => Sha512Box.Text,
                _ => string.Empty,
            };
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
    }
}
