using Microsoft.UI.Xaml.Controls;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

public sealed partial class CryptoPage : Page
{
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int KeySize = 32; // 256-bit
    private const int Pbkdf2Iterations = 100_000;

    public CryptoPage()
    {
        this.InitializeComponent();
    }

    private void Page_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => CursorHelper.ApplyHandCursorToButtons(this);

    private void OnEncryptClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                StatusText.Text = "Enter a password first.";
                return;
            }

            var plainBytes = Encoding.UTF8.GetBytes(InputBox.Text ?? string.Empty);

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var iv = RandomNumberGenerator.GetBytes(IvSize);
            var key = DeriveKey(password, salt);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            // Layout: salt (16) + iv (16) + ciphertext
            var combined = new byte[SaltSize + IvSize + cipherBytes.Length];
            Buffer.BlockCopy(salt, 0, combined, 0, SaltSize);
            Buffer.BlockCopy(iv, 0, combined, SaltSize, IvSize);
            Buffer.BlockCopy(cipherBytes, 0, combined, SaltSize + IvSize, cipherBytes.Length);

            OutputBox.Text = Convert.ToBase64String(combined);
            StatusText.Text = "Encrypted.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void OnDecryptClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                StatusText.Text = "Enter a password first.";
                return;
            }

            var combined = Convert.FromBase64String(InputBox.Text ?? string.Empty);
            if (combined.Length < SaltSize + IvSize)
            {
                StatusText.Text = "Input is not valid cipher text.";
                return;
            }

            var salt = new byte[SaltSize];
            var iv = new byte[IvSize];
            var cipherBytes = new byte[combined.Length - SaltSize - IvSize];
            Buffer.BlockCopy(combined, 0, salt, 0, SaltSize);
            Buffer.BlockCopy(combined, SaltSize, iv, 0, IvSize);
            Buffer.BlockCopy(combined, SaltSize + IvSize, cipherBytes, 0, cipherBytes.Length);

            var key = DeriveKey(password, salt);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

            OutputBox.Text = Encoding.UTF8.GetString(plainBytes);
            StatusText.Text = "Decrypted.";
        }
        catch (CryptographicException)
        {
            StatusText.Text = "Wrong password, or the text was not encrypted with this tool.";
        }
        catch (FormatException)
        {
            StatusText.Text = "Input is not valid Base64 cipher text.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }

    private void OnClearClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        InputBox.Text = string.Empty;
        OutputBox.Text = string.Empty;
        PasswordBox.Password = string.Empty;
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
