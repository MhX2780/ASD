using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace ASD;

/// <summary>One request that was sent from HTTP Track: its log, its state, and the way to cancel it. Lives as long as the app.</summary>
internal sealed class TrackRun
{
    private static readonly Regex StatusLine = new(@"HTTP/\S+\s+(\d{3})\b", RegexOptions.Compiled);
    private static readonly Regex ErrorLine = new(@"^\[\s*[\d.]+ ms\]\s+ERROR\b", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>"GET https://…"; written the way the person typed it, so {{key:…}} placeholders stay placeholders.</summary>
    public readonly string Display;
    public readonly DateTime Started = DateTime.Now;
    /// <summary>Running · Finished · Failed · Cancelled</summary>
    public volatile string State = "Running";
    public volatile string? StatusCode;

    public TrackRun(string display) { Display = display; }

    public bool IsRunning => State == "Running";
    public TimeSpan Elapsed => _clock.Elapsed;
    public void Cancel() { try { _cts.Cancel(); } catch { /* already finished */ } }

    public string LogText() { lock (_gate) return _log.ToString(); }

    private void Append(string line)
    {
        lock (_gate)
        {
            _log.AppendLine(line);
            if (_log.Length > 3_000_000) _log.Remove(0, _log.Length - 2_000_000);
        }
    }

    /// <summary>Runs the request. Keeps going if the person leaves the page.</summary>
    public async Task RunAsync(HttpTrackOptions opt, Func<string, string>? mask, string? keyNote)
    {
        var token = _cts.Token;
        if (keyNote != null) Append(keyNote);
        string? lastStatus = null;
        var failed = false;
        try
        {
            await Task.Run(() => HttpTracker.RunAsync(opt, line =>
            {
                var m = StatusLine.Match(line);
                if (m.Success) lastStatus = m.Groups[1].Value;
                if (ErrorLine.IsMatch(line)) failed = true;
                Append(mask == null ? line : mask(line));
            }, token));
        }
        catch (Exception ex) { failed = true; Append("ERROR  " + ex.Message); }

        _clock.Stop();
        StatusCode = lastStatus;
        State = token.IsCancellationRequested ? "Cancelled" : (failed || lastStatus == null) ? "Failed" : "Finished";

        // a request that took 3 s or more: tell the person when it is done, wherever they are
        var slow = int.TryParse(SettingsStore.Get("notify.slowSec", "3"), out var ss) ? Math.Max(1, ss) : 3;
        if (_clock.Elapsed.TotalSeconds >= slow && !token.IsCancellationRequested)
        {
            var host = Uri.TryCreate(opt.Url.Contains("://") ? opt.Url : "https://" + opt.Url, UriKind.Absolute, out var u) ? u.Host : "request";
            var code = int.TryParse(lastStatus, out var c) ? c : 0;
            var kind = failed || code == 0 ? NoteKind.Error : code >= 500 ? NoteKind.Error : code >= 400 ? NoteKind.Warning : NoteKind.Success;
            Notifier.Post(kind, $"{opt.Method} {host}",
                $"HTTP Track · {(code == 0 ? "no reply" : lastStatus)} · {_clock.Elapsed.TotalSeconds:0.0} s", "httptrack");
        }
    }
}

internal static class TrackRuns
{
    public static readonly List<TrackRun> All = new();   // newest first; only touched on the UI thread
}

/// <summary>
/// HTTP Track, Keys Store style: "New request" on top, the requests you sent below as cards.
/// A card shows the method, address, answer and time; clicking it opens the whole log.
/// </summary>
public sealed class HttpTrackPage : ToolPage
{
    private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    private readonly StackPanel _cardsHost = new() { Spacing = 8 };
    private readonly StackPanel _empty;
    private readonly List<RunCard> _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _busy;                  // a dialog is open: ignore another click
    private string? _pendingText;        // text from the clipboard banner (a URL or a cURL command)

    /// <summary>Everything the "New request" dialog collects.</summary>
    private sealed class Form
    {
        public string Method = "GET", Url = "", Headers = "", Body = "";
        public bool Follow = true, Insecure, Decompress = true, Pretty = true;
        public int VersionIndex, TimeoutIndex = 2;
        public string Timeout = "60";
    }

    public HttpTrackPage() : base("httptrack", "HTTP Track", "")
    {
        SetHeader("HTTP Track",
            "Send a request and watch it from start to finish: DNS, TCP, TLS, the bytes on the wire, the answer and the timing.",
            ActionBtn("", "New request", () => _ = NewRequestAsync(), accent: true),
            ActionBtn("", "Clear finished", ClearFinished));

        _empty = EmptyState("", "Click New request above to send one");
        Body.Children.Add(_empty);
        Body.Children.Add(_cardsHost);

        _timer.Tick += (_, _) => { foreach (var c in _cards) c.Update(); };
        Loaded += (_, _) =>
        {
            foreach (var run in TrackRuns.All.Where(r => _cards.All(c => c.Run != r)).Reverse()) AddCard(run);
            RefreshEmpty();
            _timer.Start();
            if (_pendingText != null) { var t = _pendingText; _pendingText = null; _ = NewRequestAsync(t); }
        };
        Unloaded += (_, _) => _timer.Stop();   // requests keep running in the background
    }

    protected override void OnClipboardInput(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("curl", StringComparison.OrdinalIgnoreCase) && !t.Contains("://")) return;
        if (IsLoaded) _ = NewRequestAsync(t);
        else _pendingText = t;
    }

    private void RefreshEmpty() => _empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ───────── "New request" dialog ─────────

    private async Task NewRequestAsync(string? clipboard = null)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var form = await AskAsync(clipboard);
            if (form != null) await SendAsync(form);
        }
        catch (Exception ex) { Fail(ex); }
        finally { _busy = false; }
    }

    private async Task<Form?> AskAsync(string? clipboard)
    {
        var method = new ComboBox { MinWidth = 110, VerticalAlignment = VerticalAlignment.Top };
        foreach (var m in Methods) method.Items.Add(m);
        method.SelectedItem = SettingsStore.Get("httptrack.method", "GET");
        if (method.SelectedIndex < 0) method.SelectedIndex = 0;

        var url = Input("url", "", placeholder: "https://example.com/api/items", persist: false, multiline: false);
        url.HorizontalAlignment = HorizontalAlignment.Stretch;
        url.Text = SettingsStore.Get("httptrack.url", "");
        AutomationProperties.SetName(url, "Request URL");
        var headers = Input("headers", "Headers (Name: value, one per line)", 110, "Accept: application/json", persist: false);
        var body = Input("body", "Body (optional)", 110, persist: false);
        var curl = Input("curl", "Paste a cURL command to fill everything in", 70, "curl -X POST https://… -H 'Content-Type: application/json' -d '{…}'", persist: false);

        TextBox last = headers;
        url.GotFocus += (_, _) => last = url;
        headers.GotFocus += (_, _) => last = headers;
        body.GotFocus += (_, _) => last = body;

        var follow = Check("Follow redirects", SettingsStore.Get("httptrack.follow", "on") == "on");
        var insecure = Check("Ignore TLS certificate errors", SettingsStore.Get("httptrack.insecure", "off") == "on");
        var decompress = Check("Decompress gzip / br", SettingsStore.Get("httptrack.decompress", "on") == "on");
        var pretty = Check("Pretty-print JSON", SettingsStore.Get("httptrack.pretty", "on") == "on");
        var version = Combo("Auto (HTTP/2 → HTTP/1.1)", "HTTP/1.1 only", "HTTP/2 only");
        version.SelectedIndex = Math.Clamp(int.TryParse(SettingsStore.Get("httptrack.version", "0"), out var vi) ? vi : 0, 0, 2);
        var timeout = Combo("15", "30", "60", "120", "300");
        timeout.SelectedIndex = Math.Clamp(int.TryParse(SettingsStore.Get("httptrack.timeout", "2"), out var ti) ? ti : 2, 0, 4);

        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };

        void ImportCurl()
        {
            var r = CurlTools.Parse(curl.Text);
            if (!method.Items.Contains(r.Method)) method.Items.Add(r.Method);
            method.SelectedItem = r.Method;
            url.Text = r.Url;
            var lines = r.Headers.Select(h => $"{h.Name}: {h.Value}").ToList();
            if (!string.IsNullOrEmpty(r.User))
                lines.Add("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(r.User)));
            headers.Text = string.Join("\n", lines);
            body.Text = r.Body ?? "";
            if (r.Insecure) insecure.IsChecked = true;
            if (r.Follow) follow.IsChecked = true;
        }

        if (!string.IsNullOrEmpty(clipboard))
        {
            if (clipboard.StartsWith("curl", StringComparison.OrdinalIgnoreCase)) { curl.Text = clipboard; try { ImportCurl(); } catch { /* not a full cURL command */ } }
            else url.Text = clipboard;
        }

        var urlRow = new Grid { ColumnSpacing = 8 };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(url, 1);
        urlRow.Children.Add(method);
        urlRow.Children.Add(url);

        var curlExpander = new Expander
        {
            Header = "Import from cURL",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { curl, Btn("Fill the request from this command", () => { try { ImportCurl(); } catch (Exception ex) { problem.Text = ex.Message; problem.Visibility = Visibility.Visible; } }) },
            },
        };

        var content = new StackPanel
        {
            Spacing = 12, MinWidth = 660,
            Children =
            {
                urlRow,
                TwoCols(headers, body),
                Row(follow, insecure, decompress, pretty),
                Row(Label("Protocol:"), version, Label("Timeout (s):"), timeout),
                KeyRefs.InsertButton(() => last),
                KeyRefs.HintText(),
                curlExpander,
                problem,
            },
        };

        var dlg = MakeDialog("New request");
        dlg.Content = content;
        dlg.PrimaryButtonText = "Send";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.PrimaryButtonClick += (_, a) =>
        {
            if (url.Text.Trim().Length == 0) { problem.Text = "Enter a URL first."; problem.Visibility = Visibility.Visible; a.Cancel = true; }
        };
        dlg.Opened += (_, _) => { CursorHelper.ApplyHandCursorToButtons(content); url.Focus(FocusState.Programmatic); };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        var f = new Form
        {
            Method = Sel(method), Url = url.Text.Trim(), Headers = headers.Text, Body = body.Text,
            Follow = Is(follow), Insecure = Is(insecure), Decompress = Is(decompress), Pretty = Is(pretty),
            VersionIndex = Math.Max(0, version.SelectedIndex), TimeoutIndex = Math.Max(0, timeout.SelectedIndex), Timeout = Sel(timeout),
        };
        SettingsStore.SetMany(new[]
        {
            new KeyValuePair<string, string>("httptrack.method", f.Method),
            new KeyValuePair<string, string>("httptrack.url", f.Url),
            new KeyValuePair<string, string>("httptrack.follow", f.Follow ? "on" : "off"),
            new KeyValuePair<string, string>("httptrack.insecure", f.Insecure ? "on" : "off"),
            new KeyValuePair<string, string>("httptrack.decompress", f.Decompress ? "on" : "off"),
            new KeyValuePair<string, string>("httptrack.pretty", f.Pretty ? "on" : "off"),
            new KeyValuePair<string, string>("httptrack.version", f.VersionIndex.ToString()),
            new KeyValuePair<string, string>("httptrack.timeout", f.TimeoutIndex.ToString()),
        });
        return f;
    }

    private static HttpTrackOptions ReadOptions(Form f)
    {
        var o = new HttpTrackOptions
        {
            Method = f.Method,
            Url = f.Url,
            Body = f.Body.Length > 0 ? f.Body : null,
            Follow = f.Follow,
            Insecure = f.Insecure,
            Decompress = f.Decompress,
            PrettyJson = f.Pretty,
            Version = f.VersionIndex switch { 1 => "1.1", 2 => "2", _ => "auto" },
            TimeoutSec = int.TryParse(f.Timeout, out var t) ? t : 60,
        };
        foreach (var raw in f.Headers.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            o.Headers.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
        }
        return o;
    }

    private async Task SendAsync(Form form)
    {
        var opt = ReadOptions(form);
        if (opt.Url.Length == 0) { Status.Text = "Enter a URL first."; return; }
        var display = $"{opt.Method} {opt.Url}";   // before the key values are put in

        // {{key:Name}} placeholders: the values come from the Keys Store, after the master password
        Func<string, string>? mask = null;
        string? keyNote = null;
        var names = KeyRefs.Names(opt.Url, opt.Body, string.Join("\n", opt.Headers.Select(h => h.Name + ": " + h.Value)));
        if (names.Count > 0)
        {
            if (!KeyVault.Exists)
            {
                Status.Text = "This request uses {{key:…}}, but there is no Keys Store yet. Create it from the Keys Store page first.";
                return;
            }
            var why = "This request uses " + (names.Count == 1 ? "a key" : $"{names.Count} keys") + " from the Keys Store (" + string.Join(", ", names) + "). Unlock it to send.";
            if (!await KeyUnlock.EnsureAsync(this, why)) { Status.Text = "Not sent: the Keys Store stayed locked."; return; }

            var set = KeyRefs.Resolve(names);
            if (set.Missing.Count > 0) { Status.Text = "No key named: " + string.Join(", ", set.Missing) + "."; return; }

            opt.Url = KeyRefs.Replace(opt.Url, set);
            opt.Body = opt.Body == null ? null : KeyRefs.Replace(opt.Body, set);
            for (var i = 0; i < opt.Headers.Count; i++)
                opt.Headers[i] = (KeyRefs.Replace(opt.Headers[i].Name, set), KeyRefs.Replace(opt.Headers[i].Value, set));
            mask = KeyRefs.Masker(set);
            keyNote = "KEYS   " + string.Join(", ", set.Values.Keys) + " read from the Keys Store; the values are hidden in this log."
                    + (set.Expired.Count > 0 ? "\n       ⚠ expired: " + string.Join(", ", set.Expired) : "");
        }

        var run = new TrackRun(display);
        TrackRuns.All.Insert(0, run);
        if (TrackRuns.All.Count > 100) TrackRuns.All.RemoveAt(TrackRuns.All.Count - 1);
        AddCard(run);
        Status.Text = "Request sent.";
        _ = run.RunAsync(opt, mask, keyNote);   // keeps running if the person leaves this page
    }

    // ───────── the list ─────────

    private void AddCard(TrackRun run)
    {
        var card = new RunCard(run, RemoveCard, OpenLogAsync);
        _cards.Add(card);
        _cardsHost.Children.Insert(0, card.Root);
        card.Update();
        RefreshEmpty();
        if (IsLoaded) CursorHelper.ApplyHandCursorToButtons(card.Root);
    }

    private void RemoveCard(RunCard card)
    {
        if (card.Run.IsRunning) card.Run.Cancel();
        TrackRuns.All.Remove(card.Run);
        _cards.Remove(card);
        _cardsHost.Children.Remove(card.Root);
        RefreshEmpty();
    }

    private void ClearFinished()
    {
        foreach (var c in _cards.Where(c => !c.Run.IsRunning).ToList()) RemoveCard(c);
    }

    /// <summary>Clicking a card: the whole log, filling in live while the request runs.</summary>
    private async Task OpenLogAsync(RunCard card)
    {
        if (_busy) return;
        _busy = true;
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        try
        {
            var box = Output("Log", 480);
            box.Text = card.Run.LogText();

            var shown = box.Text.Length;
            tick.Tick += (_, _) =>
            {
                var text = card.Run.LogText();
                if (text.Length == shown) return;
                shown = text.Length;
                box.Text = text;
                ScrollToEnd(box);
            };
            tick.Start();

            var dlg = MakeDialog(card.Run.Display);
            dlg.Content = new StackPanel { MinWidth = 760, Children = { box } };
            dlg.PrimaryButtonText = "Copy log";
            dlg.SecondaryButtonText = "Save log…";
            dlg.CloseButtonText = "Close";
            dlg.PrimaryButtonClick += (_, a) => { a.Cancel = true; Copy(card.Run.LogText()); };
            dlg.SecondaryButtonClick += (_, a) => { a.Cancel = true; _ = SaveLogAsync(card.Run.LogText()); };
            dlg.Opened += (_, _) => ScrollToEnd(box);
            await dlg.ShowAsync();
        }
        catch (Exception ex) { Fail(ex); }
        finally { tick.Stop(); _busy = false; }
    }

    private static void ScrollToEnd(TextBox box)
    {
        try
        {
            box.UpdateLayout();
            var sv = FindScroller(box);
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

    private async Task SaveLogAsync(string text)
    {
        try
        {
            if (text.Length == 0) { Status.Text = "Nothing to save yet."; return; }
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "http-track-log" };
            picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            await FileIO.WriteTextAsync(file, text);
            Status.Text = $"Saved ✔  {file.Path}";
        }
        catch (Exception ex) { Fail(ex); }
    }

    // ───────── one request card ─────────

    private sealed class RunCard
    {
        public readonly TrackRun Run;
        public readonly Border Root;

        private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _line = new() { FontSize = 12, Opacity = 0.8 };
        private readonly ProgressBar _bar = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly Button _cancel, _remove;

        public RunCard(TrackRun run, Action<RunCard> onRemove, Func<RunCard, Task> onOpen)
        {
            Run = run;
            _title.Text = run.Display;

            _cancel = Small("Cancel");
            _cancel.Click += (_, _) => Run.Cancel();
            _remove = Small("Remove");
            _remove.Click += (_, _) => onRemove(this);

            var left = new StackPanel { Spacing = 6, Children = { _title, _bar, _line } };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                Children = { _cancel, _remove },
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(left);
            grid.Children.Add(buttons);

            Root = new Border
            {
                Padding = new Thickness(16, 12, 16, 12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.4 },
                Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.08 },
                Child = grid,
            };
            Root.Tapped += async (_, e) => { if (!e.Handled) await onOpen(this); };
        }

        /// <summary>A button inside the card must not also count as a click on the card.</summary>
        private static Button Small(string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4) };
            b.Tapped += (_, e) => e.Handled = true;
            return b;
        }

        public void Update()
        {
            var r = Run;
            var seconds = r.Elapsed.TotalSeconds;
            _line.Text = r.State switch
            {
                "Running" => $"Running…  {seconds:0.0} s",
                "Cancelled" => $"Cancelled after {seconds:0.0} s",
                "Failed" => (r.StatusCode != null ? $"HTTP {r.StatusCode}  ·  " : "No reply  ·  ") + $"{seconds:0.0} s",
                _ => $"HTTP {r.StatusCode}  ·  {seconds:0.0} s",
            };
            _bar.Visibility = r.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            _cancel.Visibility = r.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
