using Microsoft.UI.Xaml;

namespace ASD;

/// <param name="Group">"main" = own item in the navigation bar (Home), "more" = listed in the Toolbox, "system" = internal.</param>
public sealed record ToolInfo(
    string Tag, string Title, string Description, string Glyph,
    string Group, string Keywords, Func<UIElement> Create)
{
    public string NavTag => Group == "more" ? "more" : Tag;

    /// <summary>The Toolbox category this tool is listed under (the middle part of "Toolbox › Web › HTTP Track").</summary>
    public string Category => ToolRegistry.CategoryOf(Tag);
}

public static class ToolRegistry
{
    /// <summary>Tools that are part of ASD.</summary>
    private static readonly List<ToolInfo> Base = new List<ToolInfo>
    {
        // ── Home (own navigation item) ──
        new("home", "Home", "Favorites and quick access", "\uE80F", "main",
            "home start welcome favorites", () => new HomePage()),

        // ── Keys Store (own navigation item, above Video Player, not in the Toolbox) ──
        new("keys", "Keys Store", "Encrypted vault for API keys: Windows Hello, search, tags, expiry", "\uE72E", "main",
            "keys key store vault api secret token password credential windows hello passkey encrypt safe", () => new KeysStorePage()),

        // ── Video Player (own navigation item, not in the Toolbox) ──
        new("video", "Video Player", "Play MP4, or MP4 + separate MP3, from a file or link", "\uE714", "main",
            "video player play mp4 mp3 movie media stream audio quality", () => new VideoPlayerPage()),

        // ── Automation (own navigation item, not in the Toolbox) ──
        new("automation", "Automation", "Cards joined by arrows (on success, on failure, when ...) that run commands, web requests and notifications", "\uE768", "main",
            "automation automate workflow flow cells cards arrows notebook script run task pipeline schedule on success failure when", () => new AutomationPage()),

        // ── Upload File (own navigation item, not in the Toolbox) ──
        new("upload", "Upload File", "Send a file to Dropbox or to any web address (POST or PUT), with a progress bar", "\uE898", "main",
            "upload file send dropbox cloud storage post put multipart share link transfer", () => new UploadPage()),

        // ── Extensions (own navigation item): the store; each extension opens its own small window ──
        new("extensions", "Extensions", "Add tools to ASD from the extensions store: search, install, update, remove", "\uEA86", "main",
            "extensions add-ons plugins store install update marketplace", () => new ExtensionsPage()),

        // ── Toolbox: every tool ──
        new("ico", "Image → ICO", "PNG, JPG, SVG … to a multi-size icon.ico", "\uE91B", "more",
            "image icon ico png jpg jpeg svg bmp gif tiff webp convert favicon", () => new IcoPage()),
        new("encode", "Encode / Decode", "Base64, URL, Hex, ROT13", "\uE8C8", "more",
            "base64 url hex rot13 escape unescape", () => new EncodePage()),
        new("crypto", "Encrypt / Decrypt", "AES-256 with a password", "\uE72E", "more",
            "aes encrypt decrypt password cipher", () => new CryptoPage()),
        new("hash", "Hash / HMAC", "MD5, SHA-1/256/384/512, HMAC, files", "\uE73E", "more",
            "md5 sha sha256 sha512 hmac checksum file", () => new HashPage()),
        new("json", "JSON Toolkit", "Format, validate, JSONPath, YAML, CSV", "\uE943", "more",
            "json format minify validate jsonpath yaml csv sort", () => new JsonPage()),
        new("jwt", "JWT Decoder", "Decode, verify and sign tokens", "\uE192", "more",
            "jwt token bearer decode verify hs256 rs256 claims", () => new JwtPage()),
        new("regex", "Regex Tester", "Live matches, groups, replace, explain", "\uE721", "more",
            "regex regexp pattern match replace", () => new RegexPage()),
        new("more", "Toolbox", "All tools", "\uEC7A", "system", "toolbox tools all", () => new MorePage()),

        // ── Also in the Toolbox ──
        new("diff", "Diff Checker", "Compare two texts or JSON files", "\uE8AB", "more",
            "diff compare difference text json", () => new DiffPage()),
        new("track", "Process Tracker", "Follow an app from launch to exit: CPU, memory, children", "\uE9D9", "more",
            "process track monitor watch cpu memory launch attach task exit child handles threads", () => new ProcessTrackerPage()),
        new("imgconv", "Image Converter", "PNG ↔ JPG ↔ BMP ↔ GIF ↔ TIFF ↔ ICO ↔ SVG", "\uEB9F", "more",
            "image convert png jpg bmp gif tiff ico svg format batch", () => new ImageConverterPage()),
        new("time", "Timestamp Converter", "Unix ↔ date, time zones, relative", "\uE121", "more",
            "timestamp unix epoch date time timezone iso", () => new TimestampPage()),
        new("gen", "Generators", "UUID v4/v7, ULID, passwords, tokens", "\uE8B1", "more",
            "uuid guid ulid password random token secret generator", () => new GeneratorsPage()),
        new("cron", "Cron Explainer", "Explain cron and see next runs", "\uE787", "more",
            "cron schedule crontab job", () => new CronPage()),
        new("conv", "Converters", "Number bases and colors", "\uE8EF", "more",
            "hex binary decimal octal base color rgb hsl", () => new ConvertersPage()),
        new("url", "URL Parser", "Split and edit URLs and query strings", "\uE774", "more",
            "url uri query string parameters parse", () => new UrlPage()),
        new("format", "Formatters", "XML, HTML and SQL", "\uE8D2", "more",
            "xml html sql format beautify minify pretty", () => new FormatPage()),
        new("curl", "cURL → Code", "Convert cURL to C#, JS, Python, PowerShell", "\uE756", "more",
            "curl http request fetch httpclient requests convert key keys store secret", () => new CurlPage()),
        new("httptrack", "HTTP Track", "Follow a request from send to reply: DNS, TCP, TLS, wire bytes, response, timing", "\uE895", "more",
            "http track trace request response log dns tcp tls ssl redirect headers timing ttfb latency wire debug inspect api key keys store secret", () => new HttpTrackPage()),
        new("httpdl", "HTTP Download", "Chunked parallel downloads with speed, size, ETA, pause and resume", "\uE896", "more",
            "http download downloader chunk chunks parallel speed resume pause file size eta get wget sha256 idm key keys store secret", () => new HttpDownloadPage()),
        new("text", "Text Tools", "Case, counters, cleanup", "\uE8FD", "more",
            "text case upper lower count words lines trim", () => new TextToolsPage()),

        new("settings", "Settings", "Theme and preferences", "\uE713", "system", "settings theme clipboard", () => new SettingsPage()),
    };

    private static readonly List<ToolInfo> Extra = new();

    // ── Toolbox categories ──
    public const string ExtensionsCategory = "Extensions";

    /// <summary>The order the category cards are shown in.</summary>
    public static readonly string[] CategoryOrder = { "Web", "Text & Data", "Security", "Images & Media", "Developer", ExtensionsCategory };

    private static readonly Dictionary<string, string> CategoryByTag = new()
    {
        ["curl"] = "Web", ["httptrack"] = "Web", ["httpdl"] = "Web", ["url"] = "Web",
        ["json"] = "Text & Data", ["regex"] = "Text & Data", ["diff"] = "Text & Data", ["encode"] = "Text & Data",
        ["format"] = "Text & Data", ["text"] = "Text & Data", ["conv"] = "Text & Data", ["time"] = "Text & Data",
        ["crypto"] = "Security", ["hash"] = "Security", ["jwt"] = "Security",
        ["ico"] = "Images & Media", ["imgconv"] = "Images & Media",
        ["cron"] = "Developer", ["gen"] = "Developer", ["track"] = "Developer",
    };

    /// <summary>A tool that is not in the list above (one that an extension added) is under "Extensions".</summary>
    public static string CategoryOf(string tag) => CategoryByTag.TryGetValue(tag, out var c) ? c : ExtensionsCategory;

    public static string CategoryGlyph(string category) => category switch
    {
        "Web" => "\uE774",
        "Text & Data" => "\uE8FD",
        "Security" => "\uE72E",
        "Images & Media" => "\uEB9F",
        "Developer" => "\uE943",
        _ => "\uEA86",
    };

    /// <summary>Every tool: ASD's own and the ones added by installed extensions.</summary>
    public static IReadOnlyList<ToolInfo> All => Extra.Count == 0 ? Base : Base.Concat(Extra).ToList();

    /// <summary>Only the tools that extensions added.</summary>
    public static IReadOnlyList<ToolInfo> ExtensionTools => Extra;

    public static void AddExtension(ToolInfo tool)
    {
        if (Base.Any(t => t.Tag == tool.Tag) || Extra.Any(t => t.Tag == tool.Tag)) return;
        Extra.Add(tool);
    }

    public static ToolInfo? Find(string tag) => All.FirstOrDefault(t => t.Tag == tag);

    public static HashSet<string> Favorites()
        => SettingsStore.Get("Favorites", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>The tools opened most recently (newest first), for Home.</summary>
    public static List<ToolInfo> Recent(int max)
        => SettingsStore.Get("Recent", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Find).Where(t => t != null && t.Group != "system" && t.Tag != "home").Select(t => t!).Take(max).ToList();

    public static void NoteUsed(string tag)
    {
        if (tag is "home" or "more" or "settings") return;
        var list = SettingsStore.Get("Recent", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(t => t != tag).ToList();
        list.Insert(0, tag);
        SettingsStore.Set("Recent", string.Join(",", list.Take(8)));
    }

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
