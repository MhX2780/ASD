using Microsoft.UI.Xaml;

namespace ASD;

/// <param name="Group">"main" = top navigation, "more" = listed on the More page, "system" = internal.</param>
public sealed record ToolInfo(
    string Tag, string Title, string Description, string Glyph,
    string Group, string Keywords, Func<UIElement> Create)
{
    public string NavTag => Group == "more" ? "more" : Tag;
}

public static class ToolRegistry
{
    public static readonly IReadOnlyList<ToolInfo> All = new List<ToolInfo>
    {
        // ── Top navigation (7 tools + "More" = 8 items) ──
        new("encode", "Encode / Decode", "Base64, URL, Hex, ROT13", "\uE8C8", "main",
            "base64 url hex rot13 escape unescape", () => new EncodePage()),
        new("crypto", "Encrypt / Decrypt", "AES-256 with a password", "\uE72E", "main",
            "aes encrypt decrypt password cipher", () => new CryptoPage()),
        new("hash", "Hash / HMAC", "MD5, SHA-1/256/384/512, HMAC, files", "\uE73E", "main",
            "md5 sha sha256 sha512 hmac checksum file", () => new HashPage()),
        new("json", "JSON Toolkit", "Format, validate, JSONPath, YAML, CSV", "\uE943", "main",
            "json format minify validate jsonpath yaml csv sort", () => new JsonPage()),
        new("jwt", "JWT Decoder", "Decode, verify and sign tokens", "\uE192", "main",
            "jwt token bearer decode verify hs256 rs256 claims", () => new JwtPage()),
        new("regex", "Regex Tester", "Live matches, groups, replace, explain", "\uE721", "main",
            "regex regexp pattern match replace", () => new RegexPage()),
        new("diff", "Diff Checker", "Compare two texts or JSON files", "\uE8AB", "main",
            "diff compare difference text json", () => new DiffPage()),
        new("more", "More", "All other tools", "\uE712", "system", "", () => new MorePage()),

        // ── Listed on the "More" page ──
        new("time", "Timestamp Converter", "Unix ↔ date, time zones, relative", "\uE121", "more",
            "timestamp unix epoch date time timezone iso", () => new TimestampPage()),
        new("gen", "Generators", "UUID v4/v7, ULID, passwords, tokens", "\uE8B7", "more",
            "uuid guid ulid password random token secret generator", () => new GeneratorsPage()),
        new("cron", "Cron Explainer", "Explain cron and see next runs", "\uE916", "more",
            "cron schedule crontab job", () => new CronPage()),
        new("conv", "Converters", "Number bases and colors", "\uE8EF", "more",
            "hex binary decimal octal base color rgb hsl", () => new ConvertersPage()),
        new("url", "URL Parser", "Split and edit URLs and query strings", "\uE774", "more",
            "url uri query string parameters parse", () => new UrlPage()),
        new("format", "Formatters", "XML, HTML and SQL", "\uE8D2", "more",
            "xml html sql format beautify minify pretty", () => new FormatPage()),
        new("curl", "cURL → Code", "Convert cURL to C#, JS, Python, PowerShell", "\uE756", "more",
            "curl http request fetch httpclient requests convert", () => new CurlPage()),
        new("text", "Text Tools", "Case, counters, cleanup", "\uE8FD", "more",
            "text case upper lower count words lines trim", () => new TextToolsPage()),

        new("settings", "Settings", "Theme and preferences", "\uE713", "system", "settings theme clipboard", () => new SettingsPage()),
    };

    public static ToolInfo? Find(string tag) => All.FirstOrDefault(t => t.Tag == tag);

    public static HashSet<string> Favorites()
        => SettingsStore.Get("Favorites", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static void ToggleFavorite(string tag)
    {
        var favs = Favorites();
        if (!favs.Remove(tag)) favs.Add(tag);
        SettingsStore.Set("Favorites", string.Join(",", favs));
    }

    /// <summary>Tools for the Ctrl+K palette. Empty query = favorites first, then everything.</summary>
    public static IEnumerable<ToolInfo> Search(string? query)
    {
        var pool = All.Where(t => t.Tag != "more");
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            var favs = Favorites();
            return pool.OrderBy(t => favs.Contains(t.Tag) ? 0 : 1);
        }
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return pool
            .Where(t => { var hay = $"{t.Title} {t.Description} {t.Keywords}".ToLowerInvariant(); return terms.All(hay.Contains); })
            .OrderBy(t => t.Title.ToLowerInvariant().StartsWith(terms[0]) ? 0 : 1);
    }
}
