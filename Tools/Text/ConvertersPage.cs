using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ASD;

public sealed class ConvertersPage : ToolPage
{
    private readonly TextBox _num, _color;
    private readonly ComboBox _from;
    private readonly TextBox _bin, _oct, _dec, _hex, _b36, _cHex, _cRgb, _cHsl;
    private readonly Border _swatch = new() { Width = 96, Height = 40, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) };

    public ConvertersPage() : base("conv", "Converters", "Number bases (binary, octal, decimal, hex, base36) and colors (HEX, RGB, HSL).")
    {
        _num = Input("num", "Number", placeholder: "255   0xFF   0b1111_1111   0o377", multiline: false);
        _from = Combo("Auto (0x / 0b / 0o prefix, else decimal)", "Binary (2)", "Octal (8)", "Decimal (10)", "Hex (16)", "Base36");
        _from.MinWidth = 320;

        Body.Children.Add(Label("Number base"));
        Body.Children.Add(Row(_num, _from));
        _num.Width = 300;
        var numRows = new StackPanel { Spacing = 6 };
        _bin = ResultRow(numRows, "Binary");
        _oct = ResultRow(numRows, "Octal");
        _dec = ResultRow(numRows, "Decimal");
        _hex = ResultRow(numRows, "Hex");
        _b36 = ResultRow(numRows, "Base36");
        Body.Children.Add(numRows);

        Body.Children.Add(Label("Color"));
        _color = Input("color", "", placeholder: "#3498db   rgb(52, 152, 219)   hsl(204, 70%, 53%)", multiline: false);
        _color.Width = 380;
        Body.Children.Add(Row(_color, _swatch));
        var colorRows = new StackPanel { Spacing = 6 };
        _cHex = ResultRow(colorRows, "HEX");
        _cRgb = ResultRow(colorRows, "RGB");
        _cHsl = ResultRow(colorRows, "HSL");
        Body.Children.Add(colorRows);

        _num.TextChanged += (_, _) => ConvertNumber();
        _from.SelectionChanged += (_, _) => ConvertNumber();
        _color.TextChanged += (_, _) => ConvertColor();
    }

    // ───────── numbers ─────────
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    private void ConvertNumber()
    {
        var s = (_num.Text ?? "").Trim().Replace("_", "").Replace(" ", "");
        if (s.Length == 0) { foreach (var b in new[] { _bin, _oct, _dec, _hex, _b36 }) b.Text = ""; Status.Text = ""; return; }
        try
        {
            bool neg = s.StartsWith("-");
            if (neg || s.StartsWith("+")) s = s[1..];
            int radix = Sel(_from) switch
            {
                "Binary (2)" => 2, "Octal (8)" => 8, "Decimal (10)" => 10, "Hex (16)" => 16, "Base36" => 36, _ => 0,
            };
            var lower = s.ToLowerInvariant();
            if (lower.StartsWith("0x") && (radix == 0 || radix == 16)) { radix = 16; s = s[2..]; }
            else if (lower.StartsWith("0b") && (radix == 0 || radix == 2)) { radix = 2; s = s[2..]; }
            else if (lower.StartsWith("0o") && (radix == 0 || radix == 8)) { radix = 8; s = s[2..]; }
            else if (radix == 0) radix = 10;

            BigInteger v = BigInteger.Zero;
            foreach (var ch in s.ToLowerInvariant())
            {
                int d = Digits.IndexOf(ch);
                if (d < 0 || d >= radix) throw new FormatException($"'{ch}' is not a valid base-{radix} digit.");
                v = v * radix + d;
            }
            if (neg) v = -v;

            _bin.Text = GroupBits(ToBase(v, 2));
            _oct.Text = ToBase(v, 8);
            _dec.Text = v.ToString();
            _hex.Text = ToBase(v, 16).ToUpperInvariant();
            _b36.Text = ToBase(v, 36).ToUpperInvariant();
            Status.Text = v.IsZero ? "" : $"{BigInteger.Abs(v).GetBitLength()} bits";
        }
        catch (Exception ex)
        {
            foreach (var b in new[] { _bin, _oct, _dec, _hex, _b36 }) b.Text = "";
            Status.Text = "⚠ " + ex.Message;
        }
    }

    private static string ToBase(BigInteger v, int radix)
    {
        if (v.IsZero) return "0";
        bool neg = v.Sign < 0;
        v = BigInteger.Abs(v);
        var sb = new StringBuilder();
        while (!v.IsZero)
        {
            sb.Insert(0, Digits[(int)(v % radix)]);
            v /= radix;
        }
        return (neg ? "-" : "") + sb;
    }

    private static string GroupBits(string bin)
    {
        bool neg = bin.StartsWith("-");
        if (neg) bin = bin[1..];
        bin = bin.PadLeft((bin.Length + 3) / 4 * 4, '0');
        var groups = Enumerable.Range(0, bin.Length / 4).Select(i => bin.Substring(i * 4, 4));
        return (neg ? "-" : "") + string.Join(" ", groups);
    }

    // ───────── colors ─────────
    private void ConvertColor()
    {
        var s = (_color.Text ?? "").Trim();
        if (s.Length == 0) { _cHex.Text = _cRgb.Text = _cHsl.Text = ""; _swatch.Background = null; return; }
        if (!TryParseColor(s, out var r, out var g, out var b, out var a))
        {
            Status.Text = "⚠ Unrecognized color. Try #RRGGBB, rgb(r,g,b) or hsl(h,s%,l%).";
            return;
        }
        Status.Text = "";
        _swatch.Background = new SolidColorBrush(Color.FromArgb(a, (byte)r, (byte)g, (byte)b));
        _cHex.Text = a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{r:X2}{g:X2}{b:X2}{a:X2}";
        _cRgb.Text = a == 255 ? $"rgb({r}, {g}, {b})" : $"rgba({r}, {g}, {b}, {a / 255.0:0.##})";
        var (h, sat, l) = RgbToHsl(r, g, b);
        _cHsl.Text = $"hsl({h:0}, {sat * 100:0}%, {l * 100:0}%)";
    }

    private static bool TryParseColor(string s, out int r, out int g, out int b, out byte a)
    {
        r = g = b = 0; a = 255;
        var hex = Regex.Match(s, @"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
            r = Convert.ToInt32(h[..2], 16); g = Convert.ToInt32(h[2..4], 16); b = Convert.ToInt32(h[4..6], 16);
            if (h.Length == 8) a = Convert.ToByte(h[6..8], 16);
            return true;
        }
        var rgb = Regex.Match(s, @"^rgba?\(\s*(\d{1,3})[,\s]+(\d{1,3})[,\s]+(\d{1,3})(?:[,\s/]+([\d.]+))?\s*\)$", RegexOptions.IgnoreCase);
        if (rgb.Success)
        {
            r = Math.Min(255, int.Parse(rgb.Groups[1].Value)); g = Math.Min(255, int.Parse(rgb.Groups[2].Value)); b = Math.Min(255, int.Parse(rgb.Groups[3].Value));
            if (rgb.Groups[4].Success) a = (byte)Math.Round(Math.Clamp(double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture), 0, 1) * 255);
            return true;
        }
        var hsl = Regex.Match(s, @"^hsla?\(\s*([\d.]+)(?:deg)?[,\s]+([\d.]+)%[,\s]+([\d.]+)%\s*(?:[,\s/]+([\d.]+))?\s*\)$", RegexOptions.IgnoreCase);
        if (hsl.Success)
        {
            double H = double.Parse(hsl.Groups[1].Value, CultureInfo.InvariantCulture) % 360;
            double S = Math.Clamp(double.Parse(hsl.Groups[2].Value, CultureInfo.InvariantCulture) / 100, 0, 1);
            double L = Math.Clamp(double.Parse(hsl.Groups[3].Value, CultureInfo.InvariantCulture) / 100, 0, 1);
            (r, g, b) = HslToRgb(H, S, L);
            if (hsl.Groups[4].Success) a = (byte)Math.Round(Math.Clamp(double.Parse(hsl.Groups[4].Value, CultureInfo.InvariantCulture), 0, 1) * 255);
            return true;
        }
        return false;
    }

    private static (double h, double s, double l) RgbToHsl(int ri, int gi, int bi)
    {
        double r = ri / 255.0, g = gi / 255.0, b = bi / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0, d = max - min;
        if (d > 0)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
            : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
