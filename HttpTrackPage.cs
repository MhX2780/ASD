using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace ASD;

public sealed class HttpTrackPage : ToolPage
{
    private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    private readonly ComboBox _method, _version, _timeout;
    private readonly TextBox _url, _headers, _body, _curl, _log;
    private readonly CheckBox _follow, _insecure, _decompress, _pretty;
    private readonly Button _send, _cancel;

    private readonly StringBuilder _sb = new();
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? _cts;

    public HttpTrackPage() : base("httptrack", "HTTP Track",
        "Send a request and watch it from start to finish: DNS, TCP, TLS, the bytes on the wire, the server's answer and a timing breakdown — every redirect included.")
    {
        _method = new ComboBox { MinWidth = 110 };
        foreach (var m in Methods) _method.Items.Add(m);
        _method.SelectedIndex = 0;

        _url = Input("url", "", placeholder: "https://example.com/api/items", multiline: false);
        _url.HorizontalAlignment = HorizontalAlignment.Stretch;
        _headers = Input("headers", "Headers (Name: value, one per line)", 110, "Accept: application/json", persist: false);
        _body = Input("body", "Body (optional)", 110, persist: false);
        _curl = Input("curl", "Paste a cURL command to fill everything in", 70, "curl -X POST https://… -H 'Content-Type: application/json' -d '{…}'", persist: false);

        _follow = Check("Follow redirects", true);
        _insecure = Check("Ignore TLS certificate errors");
        _decompress = Check("Decompress gzip / br", true);
        _pretty = Check("Pretty-print JSON", true);

        _version = Combo("Auto (HTTP/2 → HTTP/1.1)", "HTTP/1.1 only", "HTTP/2 only");
        _timeout = Combo("15", "30", "60", "120", "300");
        _timeout.SelectedIndex = 2;

        _send = Btn("Send", () => _ = SendAsync(), true);
        _cancel = Btn("Cancel", () => _cts?.Cancel());
        _cancel.IsEnabled = false;

        _log = Output("Log", 560);

        var urlRow = new Grid { ColumnSpacing = 8 };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_url, 1);
        _method.VerticalAlignment = VerticalAlignment.Top;
        urlRow.Children.Add(_method);
        urlRow.Children.Add(_url);

        var curlExpander = new Expander
        {
            Header = "Import from cURL",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { _curl, Btn("Fill the request from this command", ImportCurl) } },
        };

        Body.Children.Add(urlRow);
        Body.Children.Add(TwoCols(_headers, _body));
        Body.Children.Add(Row(_follow, _insecure, _decompress, _pretty));
        Body.Children.Add(Row(Label("Protocol:"), _version, Label("Timeout (s):"), _timeout));
        Body.Children.Add(curlExpander);
        Body.Children.Add(Row(
            _send, _cancel,
            Btn("Clear", ClearLog),
            CopyBtn("Copy log", () => _log.Text),
            Btn("Save log…", () => _ = SaveLogAsync())));
        Body.Children.Add(_log);

        _timer.Tick += (_, _) => Flush();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => { _timer.Stop(); _cts?.Cancel(); };
    }

    protected override void OnClipboardInput(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("curl", StringComparison.OrdinalIgnoreCase)) { _curl.Text = t; ImportCurl(); }
        else if (t.Contains("://")) _url.Text = t;
    }

    // ───────── actions ─────────

    private void ImportCurl()
    {
        var r = CurlTools.Parse(_curl.Text);
        if (!_method.Items.Contains(r.Method)) _method.Items.Add(r.Method);
        _method.SelectedItem = r.Method;
        _url.Text = r.Url;

        var lines = r.Headers.Select(h => $"{h.Name}: {h.Value}").ToList();
        if (!string.IsNullOrEmpty(r.User))
            lines.Add("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(r.User)));
        _headers.Text = string.Join("\n", lines);
        _body.Text = r.Body ?? "";
        if (r.Insecure) _insecure.IsChecked = true;
        if (r.Follow) _follow.IsChecked = true;
        Status.Text = "Request filled in from the cURL command.";
    }

    private HttpTrackOptions ReadOptions()
    {
        var o = new HttpTrackOptions
        {
            Method = Sel(_method),
            Url = _url.Text.Trim(),
            Body = _body.Text.Length > 0 ? _body.Text : null,
            Follow = Is(_follow),
            Insecure = Is(_insecure),
            Decompress = Is(_decompress),
            PrettyJson = Is(_pretty),
            Version = _version.SelectedIndex switch { 1 => "1.1", 2 => "2", _ => "auto" },
            TimeoutSec = int.TryParse(Sel(_timeout), out var t) ? t : 60,
        };
        foreach (var raw in _headers.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            o.Headers.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
        }
        return o;
    }

    private async Task SendAsync()
    {
        if (_cts != null) return;
        try
        {
            var opt = ReadOptions();
            if (opt.Url.Length == 0) { Status.Text = "Enter a URL first."; return; }

            ClearLog();
            _cts = new CancellationTokenSource();
            _send.IsEnabled = false;
            _cancel.IsEnabled = true;
            Status.Text = "Running…";

            var token = _cts.Token;
            await Task.Run(() => HttpTracker.RunAsync(opt, line => _queue.Enqueue(line), token));
            Status.Text = "Finished.";
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            Flush();
            _cts?.Dispose();
            _cts = null;
            _send.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }

    private void ClearLog()
    {
        while (_queue.TryDequeue(out _)) { }
        _sb.Clear();
        _log.Text = "";
    }

    private void Flush()
    {
        var any = false;
        while (_queue.TryDequeue(out var line))
        {
            _sb.AppendLine(line);
            any = true;
        }
        if (!any) return;

        if (_sb.Length > 3_000_000) _sb.Remove(0, _sb.Length - 2_000_000);
        _log.Text = _sb.ToString();
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        try
        {
            _log.UpdateLayout();
            var sv = FindScroller(_log);
            sv?.ChangeView(null, sv.ScrollableHeight, null, true);
        }
        catch { /* cosmetic */ }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindScroller(child);
            if (found != null) return found;
        }
        return null;
    }

    private async Task SaveLogAsync()
    {
        try
        {
            if (_log.Text.Length == 0) { Status.Text = "Nothing to save yet."; return; }
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "http-track-log" };
            picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await FileIO.WriteTextAsync(file, _log.Text);
            Status.Text = $"Saved ✔  {file.Path}";
        }
        catch (Exception ex) { Fail(ex); }
    }
}
