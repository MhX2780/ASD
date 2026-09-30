using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class UrlPage : ToolPage
{
    private readonly TextBox _url, _params, _info;

    public UrlPage() : base("url", "URL Parser", "Split a URL into its parts, edit the query parameters and rebuild the URL. A bare query string works too.")
    {
        _url = Input("url", "URL or query string", 80, "https://user@example.com:8080/a/b?q=hello%20world&page=2#top");
        _params = Input("params", "Query parameters (one key=value per line, decoded — editable)", 170, persist: false);
        _info = Output("Parts", 210);

        _url.TextChanged += (_, _) => Parse();

        Body.Children.Add(_url);
        Body.Children.Add(TwoCols(_params, _info));
        Body.Children.Add(Row(
            Btn("Rebuild URL from parameters", Rebuild, true),
            CopyBtn("Copy URL", () => _url.Text)));
    }

    protected override void OnClipboardInput(string text) => _url.Text = text.Trim();

    private static string Dec(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; }
    }

    private static List<(string Key, string Value)> ParseQuery(string q)
    {
        var list = new List<(string, string)>();
        foreach (var pair in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            list.Add(i < 0 ? (Dec(pair), "") : (Dec(pair[..i]), Dec(pair[(i + 1)..])));
        }
        return list;
    }

    private void Parse()
    {
        var text = (_url.Text ?? "").Trim();
        if (text.Length == 0) { _params.Text = ""; _info.Text = ""; Status.Text = ""; return; }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && text.Contains("://"))
        {
            var q = ParseQuery(uri.Query);
            _params.Text = string.Join(Environment.NewLine, q.Select(p => $"{p.Key}={p.Value}"));
            var sb = new StringBuilder();
            sb.AppendLine($"Scheme   : {uri.Scheme}");
            if (uri.UserInfo.Length > 0) sb.AppendLine($"User     : {Dec(uri.UserInfo)}");
            sb.AppendLine($"Host     : {uri.Host}");
            sb.AppendLine($"Port     : {uri.Port}{(uri.IsDefaultPort ? " (default)" : "")}");
            sb.AppendLine($"Path     : {Dec(uri.AbsolutePath)}");
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length; i++) sb.AppendLine($"  [{i}]     {Dec(segs[i])}");
            sb.AppendLine($"Query    : {q.Count} parameter{(q.Count == 1 ? "" : "s")}");
            if (uri.Fragment.Length > 0) sb.AppendLine($"Fragment : {Dec(uri.Fragment.TrimStart('#'))}");
            _info.Text = sb.ToString().TrimEnd();
            Status.Text = "";
        }
        else if (text.Contains('=') && !text.Contains(' '))
        {
            var q = ParseQuery(text.Contains('?') ? text[(text.IndexOf('?') + 1)..] : text);
            _params.Text = string.Join(Environment.NewLine, q.Select(p => $"{p.Key}={p.Value}"));
            _info.Text = $"Query string with {q.Count} parameter{(q.Count == 1 ? "" : "s")}";
            Status.Text = "";
        }
        else
        {
            _params.Text = "";
            _info.Text = "";
            Status.Text = "⚠ Not an absolute URL (include the scheme, e.g. https://).";
        }
    }

    private void Rebuild()
    {
        var text = (_url.Text ?? "").Trim();
        var pairs = (_params.Text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => { var i = l.IndexOf('='); return i < 0 ? (l, "") : (l[..i], l[(i + 1)..]); })
            .Select(p => Uri.EscapeDataString(p.Item1) + "=" + Uri.EscapeDataString(p.Item2));
        var query = string.Join("&", pairs);

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && text.Contains("://"))
        {
            var b = new UriBuilder(uri) { Query = query };
            _url.Text = b.Uri.OriginalString;
        }
        else _url.Text = query;
    }
}
