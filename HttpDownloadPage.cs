using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

public sealed class HttpDownloadPage : ToolPage
{
    private static readonly string[] LimitLabels = { "Unlimited", "256 KB/s", "512 KB/s", "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s", "25 MB/s", "50 MB/s" };
    private static readonly long[] LimitValues = { 0, 256L << 10, 512L << 10, 1L << 20, 2L << 20, 5L << 20, 10L << 20, 25L << 20, 50L << 20 };

    private readonly TextBox _urls, _headers, _folder, _fileName, _sha;
    private readonly ComboBox _conn, _limit;
    private readonly CheckBox _resume, _overwrite, _insecure;
    private readonly StackPanel _jobsHost = new() { Spacing = 10 };
    private readonly List<JobCard> _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public HttpDownloadPage() : base("httpdl", "HTTP Download",
        "Fast downloads split into parallel chunks, with live speed, size, ETA, pause / resume and optional SHA-256 check.")
    {
        _urls = Input("urls", "Download link(s) — one per line", 80, "https://example.com/big-file.zip");
        _headers = Input("headers", "Extra headers (optional — Authorization, Cookie …)", 70, "Authorization: Bearer …", persist: false);
        _folder = Input("folder", "Save to folder", placeholder: DefaultFolder, multiline: false);
        _fileName = Input("fname", "File name (optional, single link only)", placeholder: "taken from the server / URL", persist: false, multiline: false);
        _sha = Input("sha", "Expected SHA-256 (optional, single link only)", persist: false, multiline: false);

        _conn = Combo("1", "2", "4", "8", "16");
        _conn.SelectedIndex = 3;
        _limit = Combo(LimitLabels);
        _limit.SelectionChanged += (_, _) =>
        {
            var bps = LimitValues[Math.Max(0, _limit.SelectedIndex)];
            foreach (var j in HttpDownloads.Jobs.Where(j => j.IsRunning)) j.LimitBps = bps;   // applies to running downloads too
        };

        _resume = Check("Resume partial downloads", true);
        _overwrite = Check("Overwrite existing file (otherwise add “(1)”)");
        _insecure = Check("Ignore TLS certificate errors");

        Body.Children.Add(_urls);
        Body.Children.Add(TwoCols(_headers, new StackPanel { Spacing = 12, Children = { _fileName, _sha } }));
        Body.Children.Add(Grid2(_folder, Btn("Browse…", () => _ = BrowseAsync())));
        Body.Children.Add(Row(Label("Chunks (connections):"), _conn, Label("Speed limit:"), _limit));
        Body.Children.Add(Row(_resume, _overwrite, _insecure));
        Body.Children.Add(Row(
            Btn("Download", StartDownloads, true),
            Btn("Clear finished", ClearFinished),
            Btn("Open folder", OpenFolder)));
        Body.Children.Add(new TextBlock { Text = "Downloads", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        Body.Children.Add(_jobsHost);

        _timer.Tick += (_, _) => { foreach (var c in _cards) c.Update(); };
        Loaded += (_, _) =>
        {
            foreach (var job in HttpDownloads.Jobs.Where(j => _cards.All(c => c.Job != j))) AddCard(job);
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();   // downloads keep running in the background
    }

    protected override void OnClipboardInput(string text)
    {
        if (text.Contains("://")) _urls.Text = text.Trim();
    }

    private static Grid Grid2(FrameworkElement stretch, FrameworkElement auto)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(auto, 1);
        auto.VerticalAlignment = VerticalAlignment.Bottom;
        g.Children.Add(stretch);
        g.Children.Add(auto);
        return g;
    }

    // ───────── actions ─────────

    private async Task BrowseAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) _folder.Text = folder.Path;
        }
        catch (Exception ex) { Fail(ex); }
    }

    private string TargetFolder()
    {
        var f = _folder.Text.Trim();
        return f.Length == 0 ? DefaultFolder : f;
    }

    private void OpenFolder()
    {
        var f = TargetFolder();
        Directory.CreateDirectory(f);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f}\"") { UseShellExecute = true });
    }

    private void StartDownloads()
    {
        var urls = _urls.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(u => u.Contains("://") ? u : "https://" + u)
                             .ToList();
        if (urls.Count == 0) { Status.Text = "Paste at least one download link."; return; }
        var bad = urls.FirstOrDefault(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"));
        if (bad != null) { Status.Text = $"Not a valid http/https link: {bad}"; return; }

        var headers = new List<(string, string)>();
        foreach (var raw in _headers.Text.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf(':');
            if (line.Length == 0 || line.StartsWith('#') || i <= 0) continue;
            headers.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
        }

        var single = urls.Count == 1;
        foreach (var url in urls)
        {
            var o = new HttpDownloadOptions
            {
                Url = url,
                Folder = TargetFolder(),
                FileName = single && _fileName.Text.Trim().Length > 0 ? _fileName.Text.Trim() : null,
                Connections = int.TryParse(Sel(_conn), out var n) ? n : 8,
                SpeedLimitBps = LimitValues[Math.Max(0, _limit.SelectedIndex)],
                Headers = new List<(string Name, string Value)>(headers),
                Resume = Is(_resume),
                Overwrite = Is(_overwrite),
                Insecure = Is(_insecure),
                Sha256 = single && _sha.Text.Trim().Length > 0 ? _sha.Text.Trim() : null,
            };
            var job = HttpDownloads.Add(o);
            AddCard(job);
            job.Start();
        }
        Status.Text = single ? "Download started." : $"{urls.Count} downloads started.";
    }

    private void AddCard(HttpDownloadJob job)
    {
        var card = new JobCard(job, RemoveCard);
        _cards.Add(card);
        _jobsHost.Children.Insert(0, card.Root);
        card.Update();
        if (IsLoaded) CursorHelper.ApplyHandCursorToButtons(card.Root);
    }

    private void RemoveCard(JobCard card)
    {
        _cards.Remove(card);
        _jobsHost.Children.Remove(card.Root);
    }

    private void ClearFinished()
    {
        foreach (var c in _cards.Where(c => c.Job.Finished).ToList())
        {
            HttpDownloads.Remove(c.Job);
            RemoveCard(c);
        }
    }

    // ───────── one download card ─────────

    private sealed class JobCard
    {
        public readonly HttpDownloadJob Job;
        public readonly Border Root;

        private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100 };
        private readonly TextBlock _stats = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, IsTextSelectionEnabled = true };
        private readonly TextBlock _event = new() { Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _chunkHost = new() { Spacing = 4 };
        private readonly List<(ProgressBar Bar, TextBlock Txt)> _rows = new();
        private readonly Button _pause, _cancel, _open, _remove;

        public JobCard(HttpDownloadJob job, Action<JobCard> onRemove)
        {
            Job = job;

            _pause = new Button { Content = "Pause" };
            _pause.Click += (_, _) => { if (Job.IsRunning) Job.Pause(); else Job.Start(); };
            _cancel = new Button { Content = "Cancel" };
            _cancel.Click += (_, _) => Job.Cancel();
            _open = new Button { Content = "Show in folder" };
            _open.Click += (_, _) =>
            {
                try
                {
                    if (File.Exists(Job.DestPath))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Job.DestPath}\"") { UseShellExecute = true });
                    else
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Job.Opt.Folder}\"") { UseShellExecute = true });
                }
                catch { }
            };
            _remove = new Button { Content = "Remove" };
            _remove.Click += (_, _) =>
            {
                if (!Job.Finished) Job.Cancel();
                HttpDownloads.Remove(Job);
                onRemove(this);
            };

            var chunks = new Expander
            {
                Header = "Chunks",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = _chunkHost,
            };

            Root = new Border
            {
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.4 },
                Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.08 },
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        _title, _bar, _stats, _event, chunks,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _pause, _cancel, _open, _remove } },
                    },
                },
            };
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

            var sb = new StringBuilder();
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
            _stats.Text = sb.ToString().TrimEnd();

            _event.Text = j.Phase == "Failed" && j.Error != null ? "⚠ " + j.Error : j.LastEvent;

            UpdateChunks();

            var active = j.IsRunning;
            _pause.Visibility = j.Finished ? Visibility.Collapsed : Visibility.Visible;
            _pause.Content = active ? "Pause" : (j.Phase == "Failed" ? "Retry / Resume" : "Resume");
            _pause.IsEnabled = !(active && (j.Phase == "Verifying" || j.Phase == "Probing"));
            _cancel.Visibility = j.Finished ? Visibility.Collapsed : Visibility.Visible;
            _open.Visibility = j.Phase == "Done" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateChunks()
        {
            var chunks = Job.Chunks;
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
