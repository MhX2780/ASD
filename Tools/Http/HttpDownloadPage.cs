using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

/// <summary>
/// HTTP Download, Keys Store style: "New download" on top, the downloads below as cards.
/// A card shows the progress at a glance; clicking it opens everything about that download (size, speed, chunks, …).
/// "New download" opens a dialog with the link(s) and the options.
/// </summary>
public sealed class HttpDownloadPage : ToolPage
{
    private static readonly string[] LimitLabels = { "Unlimited", "256 KB/s", "512 KB/s", "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s", "25 MB/s", "50 MB/s" };
    private static readonly long[] LimitValues = { 0, 256L << 10, 512L << 10, 1L << 20, 2L << 20, 5L << 20, 10L << 20, 25L << 20, 50L << 20 };
    private static readonly string[] ConnLabels = { "1", "2", "4", "8", "16" };

    private readonly StackPanel _cardsHost = new() { Spacing = 8 };
    private readonly StackPanel _empty;
    private readonly List<JobCard> _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _starting;           // the "New download" dialog or the unlock dialog is open: ignore another click
    private string? _pendingUrl;      // a link from the clipboard banner, opened as soon as the page is shown

    private static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Everything the "New download" dialog collects.</summary>
    private sealed class Form
    {
        public string Urls = "", Headers = "", FileName = "", Sha = "", Folder = "";
        public int Connections = 8, LimitIndex;
        public bool Resume = true, Overwrite, Insecure;
    }

    public HttpDownloadPage() : base("httpdl", "HTTP Download", "")
    {
        SetHeader("HTTP Download",
            "Parallel chunks, live speed, size and ETA, pause / resume and an optional SHA-256 check.",
            ActionBtn("", "New download", () => _ = NewDownloadAsync(), accent: true),
            ActionBtn("", "Clear finished", ClearFinished),
            ActionBtn("", "Open folder", OpenFolder));

        _empty = EmptyState("", "Click New download above to start one");
        Body.Children.Add(_empty);
        Body.Children.Add(_cardsHost);

        _timer.Tick += (_, _) => { foreach (var c in _cards) c.Update(); };
        Loaded += (_, _) =>
        {
            foreach (var job in HttpDownloads.Jobs.Where(j => _cards.All(c => c.Job != j))) AddCard(job);
            RefreshEmpty();
            _timer.Start();
            if (_pendingUrl != null) { var u = _pendingUrl; _pendingUrl = null; _ = NewDownloadAsync(u); }
        };
        Unloaded += (_, _) => _timer.Stop();   // downloads keep running in the background
    }

    protected override void OnClipboardInput(string text)
    {
        if (!text.Contains("://")) return;
        if (IsLoaded) _ = NewDownloadAsync(text.Trim());
        else _pendingUrl = text.Trim();
    }

    private void RefreshEmpty() => _empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static string TargetFolder(string typed)
    {
        var f = typed.Trim();
        return f.Length == 0 ? DefaultFolder : f;
    }

    private void OpenFolder()
    {
        var f = TargetFolder(SettingsStore.Get("httpdl.folder", ""));
        Directory.CreateDirectory(f);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f}\"") { UseShellExecute = true });
    }

    private static async Task BrowseAsync(TextBox target)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) target.Text = folder.Path;
        }
        catch { /* the picker was closed */ }
    }

    // ───────── "New download" dialog ─────────

    private async Task NewDownloadAsync(string? prefill = null)
    {
        if (_starting) return;
        _starting = true;
        try
        {
            var form = await AskAsync(prefill);
            if (form != null) await StartAsync(form);
        }
        catch (Exception ex) { Fail(ex); }
        finally { _starting = false; }
    }

    private async Task<Form?> AskAsync(string? prefill)
    {
        var urls = Input("urls", "Download link(s) — one per line", 84, "https://example.com/big-file.zip", persist: false);
        AutomationProperties.SetName(urls, "Download links");
        if (!string.IsNullOrEmpty(prefill)) urls.Text = prefill;
        var headers = Input("headers", "Extra headers (optional — Authorization, Cookie …)", 70, "Authorization: Bearer …", persist: false);
        AutomationProperties.SetName(headers, "Extra headers");
        var fileName = Input("fname", "File name (optional, one link only)", placeholder: "taken from the server / URL", persist: false, multiline: false);
        var sha = Input("sha", "Expected SHA-256 (optional, one link only)", persist: false, multiline: false);
        var folder = Input("folder", "Save to folder", placeholder: DefaultFolder, persist: false, multiline: false);
        folder.Text = SettingsStore.Get("httpdl.folder", "");
        AutomationProperties.SetName(folder, "Save to folder");

        TextBox last = urls;
        urls.GotFocus += (_, _) => last = urls;
        headers.GotFocus += (_, _) => last = headers;

        var conn = Combo(ConnLabels);
        conn.SelectedIndex = Math.Clamp(Array.IndexOf(ConnLabels, SettingsStore.Get("httpdl.conn", "8")), 0, ConnLabels.Length - 1);
        var limit = Combo(LimitLabels);
        limit.SelectedIndex = Math.Clamp(int.TryParse(SettingsStore.Get("httpdl.limit", "0"), out var li) ? li : 0, 0, LimitLabels.Length - 1);
        var resume = Check("Resume partial downloads", SettingsStore.Get("httpdl.resume", "on") == "on");
        var overwrite = Check("Overwrite existing file (otherwise add “(1)”)", SettingsStore.Get("httpdl.overwrite", "off") == "on");
        var insecure = Check("Ignore TLS certificate errors", SettingsStore.Get("httpdl.insecure", "off") == "on");

        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };

        var browse = Btn("Browse…", () => _ = BrowseAsync(folder));
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(folder);
        folderRow.Children.Add(browse);

        var content = new StackPanel
        {
            Spacing = 12, MinWidth = 640,
            Children =
            {
                urls,
                TwoCols(headers, new StackPanel { Spacing = 12, Children = { fileName, sha } }),
                KeyRefs.InsertButton(() => last),
                KeyRefs.HintText(),
                folderRow,
                Row(Label("Chunks (connections):"), conn, Label("Speed limit:"), limit),
                Row(resume, overwrite, insecure),
                problem,
            },
        };

        var dlg = MakeDialog("New download");
        dlg.Content = content;
        dlg.PrimaryButtonText = "Download";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.PrimaryButtonClick += (_, a) =>
        {
            var list = SplitUrls(urls.Text);
            if (list.Count == 0) { problem.Text = "Paste at least one download link."; problem.Visibility = Visibility.Visible; a.Cancel = true; return; }
            var bad = list.FirstOrDefault(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"));
            if (bad != null) { problem.Text = $"Not a valid http/https link: {bad}"; problem.Visibility = Visibility.Visible; a.Cancel = true; }
        };
        dlg.Opened += (_, _) => { CursorHelper.ApplyHandCursorToButtons(content); urls.Focus(FocusState.Programmatic); };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        var f = new Form
        {
            Urls = urls.Text, Headers = headers.Text, FileName = fileName.Text.Trim(), Sha = sha.Text.Trim(),
            Folder = TargetFolder(folder.Text),
            Connections = int.TryParse(Sel(conn), out var n) ? n : 8,
            LimitIndex = Math.Max(0, limit.SelectedIndex),
            Resume = Is(resume), Overwrite = Is(overwrite), Insecure = Is(insecure),
        };
        SettingsStore.SetMany(new[]
        {
            new KeyValuePair<string, string>("httpdl.folder", folder.Text.Trim()),
            new KeyValuePair<string, string>("httpdl.conn", Sel(conn)),
            new KeyValuePair<string, string>("httpdl.limit", f.LimitIndex.ToString()),
            new KeyValuePair<string, string>("httpdl.resume", f.Resume ? "on" : "off"),
            new KeyValuePair<string, string>("httpdl.overwrite", f.Overwrite ? "on" : "off"),
            new KeyValuePair<string, string>("httpdl.insecure", f.Insecure ? "on" : "off"),
        });
        return f;
    }

    private static List<string> SplitUrls(string text)
        => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(u => u.Contains("://") ? u : "https://" + u)
               .ToList();

    private async Task StartAsync(Form form)
    {
        var urls = SplitUrls(form.Urls);
        if (urls.Count == 0) { Status.Text = "Paste at least one download link."; return; }

        var headers = new List<(string, string)>();
        foreach (var raw in form.Headers.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf(':');
            if (line.Length == 0 || line.StartsWith('#') || i <= 0) continue;
            headers.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
        }

        // {{key:Name}}: the values come from the Keys Store, after the master password. Only the placeholders stay in the job.
        KeyRefSet? keys = null;
        Func<string, string>? mask = null;
        var names = KeyRefs.Names(string.Join("\n", urls), string.Join("\n", headers.Select(h => h.Item1 + ": " + h.Item2)));
        if (names.Count > 0)
        {
            if (!KeyVault.Exists)
            {
                Status.Text = "These downloads use {{key:…}}, but there is no Keys Store yet. Create it from the Keys Store page first.";
                return;
            }
            var why = "These downloads use " + (names.Count == 1 ? "a key" : $"{names.Count} keys") + " from the Keys Store (" + string.Join(", ", names) + "). Unlock it to start.";
            if (!await KeyUnlock.EnsureAsync(this, why)) { Status.Text = "Not started: the Keys Store stayed locked."; return; }
            keys = KeyRefs.Resolve(names);
            if (keys.Missing.Count > 0) { Status.Text = "No key named: " + string.Join(", ", keys.Missing) + "."; return; }
            mask = KeyRefs.Masker(keys);
        }

        var single = urls.Count == 1;
        foreach (var url in urls)
        {
            var o = new HttpDownloadOptions
            {
                Url = url,
                Folder = form.Folder,
                FileName = single && form.FileName.Length > 0 ? form.FileName : null,
                Connections = form.Connections,
                SpeedLimitBps = LimitValues[Math.Clamp(form.LimitIndex, 0, LimitValues.Length - 1)],
                Headers = new List<(string Name, string Value)>(headers),
                Resume = form.Resume,
                Overwrite = form.Overwrite,
                Insecure = form.Insecure,
                Sha256 = single && form.Sha.Length > 0 ? form.Sha : null,
                Keys = keys,
                Mask = mask,
            };
            var job = HttpDownloads.Add(o);
            AddCard(job);
            job.Start();
        }
        Status.Text = single ? "Download started." : $"{urls.Count} downloads started.";
    }

    // ───────── the list ─────────

    private void AddCard(HttpDownloadJob job)
    {
        var card = new JobCard(job, RemoveCard, PrepareKeysAsync, OpenInfoAsync);
        _cards.Add(card);
        _cardsHost.Children.Insert(0, card.Root);
        card.Update();
        RefreshEmpty();
        if (IsLoaded) CursorHelper.ApplyHandCursorToButtons(card.Root);
    }

    private void RemoveCard(JobCard card)
    {
        _cards.Remove(card);
        _cardsHost.Children.Remove(card.Root);
        RefreshEmpty();
    }

    private void ClearFinished()
    {
        foreach (var c in _cards.Where(c => c.Job.Finished).ToList())
        {
            HttpDownloads.Remove(c.Job);
            RemoveCard(c);
        }
    }

    /// <summary>
    /// Before a download that uses {{key:…}} is resumed or retried: the Keys Store must be unlocked (the password is asked
    /// again if it locked meanwhile) and the values are read again, so a changed key is picked up.
    /// </summary>
    private async Task<bool> PrepareKeysAsync(HttpDownloadJob job)
    {
        if (job.Opt.Keys == null) return true;
        var names = job.Opt.Keys.Values.Keys.ToList();
        if (!await KeyUnlock.EnsureAsync(this, "This download uses " + string.Join(", ", names) + " from the Keys Store. Unlock it to continue."))
        {
            Status.Text = "Not resumed: the Keys Store stayed locked.";
            return false;
        }
        var set = KeyRefs.Resolve(names);
        if (set.Missing.Count > 0) { Status.Text = "No key named: " + string.Join(", ", set.Missing) + "."; return false; }
        job.Opt.Keys = set;
        job.Opt.Mask = KeyRefs.Masker(set);
        return true;
    }

    /// <summary>Clicking a card: everything about that download, updating live while the dialog is open.</summary>
    private async Task OpenInfoAsync(JobCard card)
    {
        if (_starting) return;
        _starting = true;
        try
        {
            var view = new JobInfoView(card.Job);
            card.Info = view;
            view.Update();

            var dlg = MakeDialog(card.Job.FileName.Length > 0 ? card.Job.FileName : "Download");
            dlg.Content = new ScrollViewer { Content = view.Root, MinWidth = 640, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dlg.PrimaryButtonText = "Show in folder";
            dlg.CloseButtonText = "Close";
            dlg.PrimaryButtonClick += (_, a) => { a.Cancel = true; card.ShowInFolder(); };   // stays open
            try { await dlg.ShowAsync(); }
            finally { card.Info = null; }
        }
        catch (Exception ex) { Fail(ex); }
        finally { _starting = false; }
    }

    // ───────── one download card ─────────

    private sealed class JobCard
    {
        public readonly HttpDownloadJob Job;
        public readonly Border Root;
        public JobInfoView? Info;

        private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100 };
        private readonly TextBlock _summary = new() { FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _problem = new() { FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
        private readonly Button _pause, _cancel, _remove;

        public JobCard(HttpDownloadJob job, Action<JobCard> onRemove, Func<HttpDownloadJob, Task<bool>> prepare, Func<JobCard, Task> onOpen)
        {
            Job = job;

            _pause = Small("Pause");
            _pause.Click += async (_, _) =>
            {
                if (Job.IsRunning) { Job.Pause(); return; }
                if (await prepare(Job)) Job.Start();
            };
            _cancel = Small("Cancel");
            _cancel.Click += (_, _) => Job.Cancel();
            _remove = Small("Remove");
            _remove.Click += (_, _) =>
            {
                if (!Job.Finished) Job.Cancel();
                HttpDownloads.Remove(Job);
                onRemove(this);
            };

            var left = new StackPanel { Spacing = 6, Children = { _title, _bar, _summary, _problem } };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                Children = { _pause, _cancel, _remove },
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

        public void ShowInFolder()
        {
            try
            {
                if (File.Exists(Job.DestPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Job.DestPath}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Job.Opt.Folder}\"") { UseShellExecute = true });
            }
            catch { /* explorer could not start */ }
        }

        public void Update()
        {
            var j = Job;
            j.Sample();

            var done = j.Downloaded;
            var total = j.TotalSize;
            var known = total > 0;

            _title.Text = $"{(j.FileName.Length > 0 ? j.FileName : j.Opt.Url)}   ·   {j.Phase}";

            _bar.IsIndeterminate = j.Phase == "Probing" || (j.Phase == "Downloading" && !known);
            if (!_bar.IsIndeterminate) _bar.Value = known ? Math.Min(100, done * 100.0 / total) : (j.Phase == "Done" ? 100 : 0);
            _bar.ShowError = j.Phase == "Failed";
            _bar.ShowPaused = j.Phase == "Paused";

            var text = SizeFmt.Bytes(done) + (known ? $" of {SizeFmt.Bytes(total)}  ({done * 100.0 / total:0}%)" : "");
            if (j.Phase == "Downloading") text += $"  ·  {SizeFmt.Speed(j.Speed)}" + (j.Eta is { } eta ? $"  ·  {SizeFmt.Duration(eta)} left" : "");
            else if (j.Phase == "Done") text += $"  ·  {SizeFmt.Duration(j.Elapsed)}  ·  average {SizeFmt.Speed(j.AvgSpeed)}";
            _summary.Text = text;

            var err = j.Phase == "Failed" && j.Error != null ? (j.Opt.Mask?.Invoke(j.Error) ?? j.Error) : "";
            _problem.Text = err;
            _problem.Visibility = err.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var active = j.IsRunning;
            _pause.Visibility = j.Finished ? Visibility.Collapsed : Visibility.Visible;
            _pause.Content = active ? "Pause" : (j.Phase == "Failed" ? "Retry" : "Resume");
            _pause.IsEnabled = !(active && (j.Phase == "Verifying" || j.Phase == "Probing"));
            _cancel.Visibility = j.Finished ? Visibility.Collapsed : Visibility.Visible;

            Info?.Update();
        }
    }

    // ───────── the details shown when a card is clicked ─────────

    private sealed class JobInfoView
    {
        public readonly StackPanel Root;
        private readonly HttpDownloadJob _job;
        private readonly TextBlock _stats = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, IsTextSelectionEnabled = true };
        private readonly TextBlock _event = new() { Opacity = 0.8, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        private readonly StackPanel _chunkHost = new() { Spacing = 4 };
        private readonly List<(ProgressBar Bar, TextBlock Txt)> _rows = new();

        public JobInfoView(HttpDownloadJob job)
        {
            _job = job;
            var chunks = new Expander
            {
                Header = "Chunks",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = _chunkHost,
            };
            Root = new StackPanel { Spacing = 10, Children = { _stats, _event, chunks } };
        }

        public void Update()
        {
            var j = _job;
            var done = j.Downloaded;
            var total = j.TotalSize;
            var known = total > 0;

            var sb = new StringBuilder();
            sb.AppendLine($"Link        {j.Opt.Url}");
            sb.AppendLine($"State       {j.Phase}");
            sb.AppendLine($"Size        {(known ? SizeFmt.Bytes(total) : "unknown")}");
            sb.AppendLine($"Downloaded  {SizeFmt.Bytes(done)}" + (known ? $"   ({done * 100.0 / total:0.0}%)" : ""));
            sb.AppendLine($"Speed       {SizeFmt.Speed(j.Speed)}   (average {SizeFmt.Speed(j.AvgSpeed)})" +
                          (j.LimitBps > 0 ? $"   ·   limit {SizeFmt.Speed(j.LimitBps)}" : ""));
            sb.AppendLine($"Time        {SizeFmt.Duration(j.Elapsed)} elapsed" + (j.Eta is { } eta ? $"   ·   about {SizeFmt.Duration(eta)} left" : ""));
            if (j.Chunks.Count > 0)
                sb.AppendLine($"Chunks      {j.Chunks.Count} × {(j.AcceptRanges && known ? "HTTP range" : "single stream")}   ·   resume {(j.AcceptRanges && known ? "supported ✔" : "not supported ✘")}" +
                              (j.Resumed ? "   ·   resumed" : ""));
            if (!string.IsNullOrEmpty(j.ContentType))
                sb.AppendLine($"Type        {j.ContentType}" + (string.IsNullOrEmpty(j.Server) ? "" : $"   ·   {j.Server}"));
            if (j.DestPath.Length > 0) sb.Append($"Saving to   {j.DestPath}");
            var text = sb.ToString().TrimEnd();
            _stats.Text = j.Opt.Mask?.Invoke(text) ?? text;

            var eventText = j.Phase == "Failed" && j.Error != null ? "⚠ " + j.Error : j.LastEvent;
            _event.Text = j.Opt.Mask?.Invoke(eventText) ?? eventText;

            UpdateChunks();
        }

        private void UpdateChunks()
        {
            var chunks = _job.Chunks;
            if (_rows.Count != chunks.Count)
            {
                _rows.Clear();
                _chunkHost.Children.Clear();
                foreach (var _ in chunks)
                {
                    var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4 };
                    var txt = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, Opacity = 0.85 };
                    _chunkHost.Children.Add(new StackPanel { Spacing = 2, Children = { txt, bar } });
                    _rows.Add((bar, txt));
                }
            }

            for (var i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                var d = Volatile.Read(ref c.Done);
                var pct = c.Length > 0 ? Math.Min(100, d * 100.0 / c.Length) : 0;
                _rows[i].Bar.Value = pct;
                _rows[i].Bar.IsIndeterminate = c.Length < 0 && c.State == "downloading";
                _rows[i].Bar.ShowError = c.State == "failed";
                var range = c.End >= 0 ? $"{SizeFmt.Bytes(c.Start)} – {SizeFmt.Bytes(c.End + 1)}" : "whole file";
                _rows[i].Txt.Text = $"#{i + 1,-2} {range,-22} {pct,5:0.0}%  {SizeFmt.Bytes(d),-10} {SizeFmt.Speed(c.Speed),-11} {c.State}";
            }
        }
    }
}
