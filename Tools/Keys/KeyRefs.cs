using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

/// <summary>The keys a piece of text refers to, resolved from the (unlocked) Keys Store.</summary>
internal sealed class KeyRefSet
{
    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Missing = new();
    public readonly List<string> Expired = new();
}

/// <summary>
/// <c>{{key:Name}}</c> references: the other tools (HTTP Track, cURL → Code) can use a key from the Keys Store
/// without the value ever being typed or saved there. Only the placeholder is stored; the value is read when it is
/// needed and only after the Keys Store is unlocked (see <see cref="KeyUnlock"/>).
/// </summary>
internal static class KeyRefs
{
    private static readonly Regex Rx = new(@"\{\{\s*key\s*:\s*([^{}]+?)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string Hint = "Use {{key:String-Value}} for a key from the Keys Store (the key's \"String value\", e.g. Azure-Key). It is read only when needed, after your master password.";

    /// <summary>"Azure Key" → "Azure-Key": spaces become dashes, anything that is not a letter, digit, - _ . is dropped.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? "").Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '.') sb.Append(c);
            else if (c == '-' || char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); }
        }
        return sb.ToString().Trim('-');
    }

    public static bool IsValidRef(string text) => text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>The string a key is referred to by: its own "string value", or one made from the name.</summary>
    public static string RefOf(KeyEntry e) => e.Ref.Trim().Length > 0 ? e.Ref.Trim() : Slug(e.Name);

    public static string Token(KeyEntry e) => "{{key:" + RefOf(e) + "}}";

    public static bool Has(string? text) => !string.IsNullOrEmpty(text) && Rx.IsMatch(text);

    public static List<string> Names(params string?[] texts)
        => texts.Where(t => !string.IsNullOrEmpty(t))
                .SelectMany(t => Rx.Matches(t!).Select(m => m.Groups[1].Value.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>Looks the names up and decrypts them. The vault must be unlocked.</summary>
    public static KeyRefSet Resolve(IEnumerable<string> names)
    {
        var set = new KeyRefSet();
        var all = KeyVault.All();
        foreach (var n in names)
        {
            var e = all.FirstOrDefault(k => string.Equals(RefOf(k), n, StringComparison.OrdinalIgnoreCase))
                 ?? all.FirstOrDefault(k => string.Equals(k.Name, n, StringComparison.OrdinalIgnoreCase));   // a plain name still works
            var value = e == null ? null : KeyVault.TryReveal(e.Id);
            if (value == null) { set.Missing.Add(n); continue; }
            set.Values[n] = value;
            if (e!.Expires is { } d && d.Date < DateTime.Today) set.Expired.Add(e.Name);
        }
        return set;
    }

    public static string Replace(string? text, KeyRefSet set)
        => text == null ? "" : Rx.Replace(text, m => set.Values.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? v : m.Value);

    /// <summary>
    /// Returns a function that puts the placeholders back wherever a key value shows up in a text (raw, URL-escaped
    /// or base64), so logs never contain the secret itself.
    /// </summary>
    public static Func<string, string> Masker(KeyRefSet set)
    {
        var pairs = new List<(string Secret, string Label)>();
        foreach (var (name, value) in set.Values)
        {
            if (value.Length < 4) continue;          // too short to hide without damaging the log
            var label = "{{key:" + name + "}}";
            pairs.Add((value, label));
            var escaped = Uri.EscapeDataString(value);
            if (escaped != value) pairs.Add((escaped, label));
            pairs.Add((System.Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), label));
        }
        pairs = pairs.OrderByDescending(p => p.Secret.Length).ToList();
        return line =>
        {
            foreach (var (secret, label) in pairs)
                if (line.Contains(secret, StringComparison.Ordinal)) line = line.Replace(secret, label, StringComparison.Ordinal);
            return line;
        };
    }

    /// <summary>A button with a menu of the key names (names are not secret, so no unlock is needed) that inserts the placeholder.</summary>
    public static Button InsertButton(Func<TextBox?> target)
    {
        var button = new Button { Content = "Insert key…" };
        ToolTipService.SetToolTip(button, "Insert a {{key:Name}} placeholder for a key from the Keys Store");
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var keys = KeyVault.Exists
                ? KeyVault.All().OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                : new List<KeyEntry>();
            if (keys.Count == 0)
                flyout.Items.Add(new MenuFlyoutItem { Text = KeyVault.Exists ? "No keys yet" : "Create your Keys Store first", IsEnabled = false });
            foreach (var key in keys)
            {
                var reference = RefOf(key);
                var item = new MenuFlyoutItem { Text = reference == key.Name ? key.Name : $"{key.Name}   ·   {reference}" };
                item.Click += (_, _) =>
                {
                    var box = target();
                    if (box == null) return;
                    var token = "{{key:" + reference + "}}";
                    var at = Math.Min(box.SelectionStart, box.Text.Length);
                    var len = Math.Min(box.SelectionLength, box.Text.Length - at);
                    box.Text = box.Text.Remove(at, len).Insert(at, token);
                    box.SelectionStart = at + token.Length;
                    box.Focus(FocusState.Programmatic);
                };
                flyout.Items.Add(item);
            }
        };
        button.Flyout = flyout;
        return button;
    }

    public static TextBlock HintText() => new()
    {
        Text = Hint, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12,
    };
}
