using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class GeneratorsPage : ToolPage
{
    private readonly ComboBox _type;
    private readonly NumberBox _count, _length, _min, _max;
    private readonly CheckBox _uuidUpper, _noHyphen, _pwUpper, _pwDigits, _pwSymbols, _pwAvoid;
    private readonly StackPanel _optUuid, _optLen, _optPw, _optNum;
    private readonly TextBox _out;

    private static readonly string[] Types =
    {
        "UUID v4", "UUID v7 (time-ordered)", "ULID", "Password", "Random string (a-z A-Z 0-9)",
        "Hex token (length = bytes)", "Base64URL token (length = bytes)", "Random integer",
    };

    public GeneratorsPage() : base("gen", "Generators", "Cryptographically secure random values — UUIDs, ULIDs, passwords, tokens and numbers.")
    {
        _type = Combo(Types);
        _type.MinWidth = 260;
        _count = Num("Count", 5, 1, 500);
        _length = Num("Length", 20, 1, 512);
        _min = Num("Min", 1, int.MinValue, int.MaxValue - 1);
        _max = Num("Max", 100, int.MinValue, int.MaxValue - 1);
        _uuidUpper = Check("UPPERCASE"); _noHyphen = Check("No hyphens");
        _pwUpper = Check("A-Z", true); _pwDigits = Check("0-9", true); _pwSymbols = Check("Symbols", true); _pwAvoid = Check("Avoid look-alikes (Il1O0)");
        _optUuid = Row(_uuidUpper, _noHyphen);
        _optLen = Row(_length);
        _optPw = Row(_pwUpper, _pwDigits, _pwSymbols, _pwAvoid);
        _optNum = Row(_min, _max);
        _out = Output("Results", 300);

        _type.SelectionChanged += (_, _) => { UpdateOptions(); Generate(); };
        foreach (var n in new[] { _count, _length, _min, _max }) n.ValueChanged += (_, _) => Generate();
        foreach (var c in new[] { _uuidUpper, _noHyphen, _pwUpper, _pwDigits, _pwSymbols, _pwAvoid }) { c.Checked += (_, _) => Generate(); c.Unchecked += (_, _) => Generate(); }

        Body.Children.Add(Row(_type, _count));
        Body.Children.Add(_optUuid);
        Body.Children.Add(_optLen);
        Body.Children.Add(_optPw);
        Body.Children.Add(_optNum);
        Body.Children.Add(Row(Btn("Generate", Generate, true), CopyBtn("Copy all", () => _out.Text)));
        Body.Children.Add(_out);
        UpdateOptions();
        Generate();
    }

    private static NumberBox Num(string header, double value, double min, double max) => new()
    {
        Header = header, Value = value, Minimum = min, Maximum = max, Width = 150,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
    };

    private static int Val(NumberBox n, int fallback) => double.IsNaN(n.Value) ? fallback : (int)n.Value;

    private void UpdateOptions()
    {
        var t = Sel(_type);
        _optUuid.Visibility = t.StartsWith("UUID") ? Visibility.Visible : Visibility.Collapsed;
        _optLen.Visibility = t is "Password" or "Random string (a-z A-Z 0-9)" or "Hex token (length = bytes)" or "Base64URL token (length = bytes)" ? Visibility.Visible : Visibility.Collapsed;
        _optPw.Visibility = t == "Password" ? Visibility.Visible : Visibility.Collapsed;
        _optNum.Visibility = t == "Random integer" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string Uuid(string s)
    {
        if (Is(_noHyphen)) s = s.Replace("-", "");
        return Is(_uuidUpper) ? s.ToUpperInvariant() : s;
    }

    private void Generate()
    {
        if (_out == null) return;   // events can fire while the page is being built
        try
        {
            int count = Math.Clamp(Val(_count, 5), 1, 500);
            int len = Math.Clamp(Val(_length, 20), 1, 512);
            var t = Sel(_type);
            var lines = new List<string>();
            double bits = 0;
            for (int i = 0; i < count; i++)
            {
                switch (t)
                {
                    case "UUID v4": lines.Add(Uuid(Guid.NewGuid().ToString())); break;
                    case "UUID v7 (time-ordered)": lines.Add(Uuid(Gen.UuidV7())); break;
                    case "ULID": lines.Add(Gen.Ulid()); break;
                    case "Password": lines.Add(Gen.Password(len, Is(_pwUpper), Is(_pwDigits), Is(_pwSymbols), Is(_pwAvoid), out bits)); break;
                    case "Random string (a-z A-Z 0-9)": lines.Add(Gen.RandomString(len)); break;
                    case "Hex token (length = bytes)": lines.Add(Convert.ToHexString(RandomNumberGenerator.GetBytes(len)).ToLowerInvariant()); break;
                    case "Base64URL token (length = bytes)":
                        lines.Add(Convert.ToBase64String(RandomNumberGenerator.GetBytes(len)).TrimEnd('=').Replace('+', '-').Replace('/', '_')); break;
                    case "Random integer":
                    {
                        int lo = Val(_min, 1), hi = Val(_max, 100);
                        if (lo > hi) (lo, hi) = (hi, lo);
                        lines.Add(RandomNumberGenerator.GetInt32(lo, hi == int.MaxValue ? hi : hi + 1).ToString());
                        break;
                    }
                }
            }
            _out.Text = string.Join(Environment.NewLine, lines);
            Status.Text = t == "Password" ? $"≈ {bits:0} bits of entropy per password" : "";
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static class Gen
    {
        public static string UuidV7()
        {
            Span<byte> b = stackalloc byte[16];
            RandomNumberGenerator.Fill(b);
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            b[0] = (byte)(ms >> 40); b[1] = (byte)(ms >> 32); b[2] = (byte)(ms >> 24);
            b[3] = (byte)(ms >> 16); b[4] = (byte)(ms >> 8); b[5] = (byte)ms;
            b[6] = (byte)((b[6] & 0x0F) | 0x70);
            b[8] = (byte)((b[8] & 0x3F) | 0x80);
            var h = Convert.ToHexString(b).ToLowerInvariant();
            return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
        }

        public static string Ulid()
        {
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            UInt128 v = (UInt128)(ulong)ms << 80;
            UInt128 r = 0;
            foreach (var by in RandomNumberGenerator.GetBytes(10)) r = (r << 8) | by;
            v |= r;
            var chars = new char[26];
            for (int i = 25; i >= 0; i--)
            {
                chars[i] = alphabet[(int)(v & 31)];
                v >>= 5;
            }
            return new string(chars);
        }

        public static string RandomString(int len)
        {
            const string set = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var c = new char[len];
            for (int i = 0; i < len; i++) c[i] = set[RandomNumberGenerator.GetInt32(set.Length)];
            return new string(c);
        }

        public static string Password(int len, bool upper, bool digits, bool symbols, bool avoid, out double bits)
        {
            var pools = new List<string> { "abcdefghijklmnopqrstuvwxyz" };
            if (upper) pools.Add("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            if (digits) pools.Add("0123456789");
            if (symbols) pools.Add("!@#$%^&*()-_=+[]{};:,.?");
            if (avoid) pools = pools.Select(p => new string(p.Where(ch => "Il1O0o|".IndexOf(ch) < 0).ToArray())).ToList();
            var all = string.Concat(pools);
            var chars = new List<char>();
            foreach (var p in pools) if (chars.Count < len) chars.Add(p[RandomNumberGenerator.GetInt32(p.Length)]);
            while (chars.Count < len) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
            for (int i = chars.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }
            bits = len * Math.Log2(all.Length);
            return new string(chars.ToArray());
        }
    }
}
