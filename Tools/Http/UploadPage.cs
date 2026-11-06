using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

/// <summary>The file picker for "any file", shared by the Upload page and the Automation cell settings.</summary>
internal static class FilePick
{
    public static async Task<string?> AnyAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch { return null; }      // the picker was closed
    }
}

/// <summary>One upload that runs in the background (it goes on while you look at another page).</summary>
internal sealed class UploadJob
{
    public readonly int Id;
    public readonly UploadOptions Opt;
    public string? AppKey, Refresh;                     // Dropbox sign-in, read from the Keys Store when the upload was created
    public Func<string, string> Mask = s => s;          // hides key values in what is shown

    public volatile string Phase = "Queued";            // Queued | Sending | Done | Failed | Cancelled
    public string Message = "";
    public string Output = "";
    public int Status;
    public DateTime Started;
    public TimeSpan Elapsed;
    public double Speed;

    private long _sent, _total;
    private long _lastSent;
    private DateTime _lastSample;
    private CancellationTokenSource? _cts;

    public UploadJob(int id, UploadOptions opt) { Id = id; Opt = opt; }

    public long Sent => Interlocked.Read(ref _sent);
    public long Total => Interlocked.Read(ref _total);
    public bool IsRunning => Phase == "Sending";
    public bool Finished => Phase is "Done" or "Failed" or "Cancelled";
    public string FileName => System.IO.Path.GetFileName(Opt.FilePath);

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _total, 0);
        _lastSent = 0; _lastSample = DateTime.UtcNow; Speed = 0;
        Message = ""; Output = ""; Status = 0;
        Started = DateTime.Now;
        Phase = "Sending";
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(ct));
    }

    public void Cancel() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var r = await UploadEngine.RunAsync(Opt, AppKey, Refresh, (sent, total) =>
            {
                Interlocked.Exchange(ref _sent, sent);
                Interlocked.Exchange(ref _total, total);
            }, ct);
            Message = Mask(r.Message);
            Output = r.Output;
            Status = r.Status;
            if (r.Ok) Interlocked.Exchange(ref _sent, Math.Max(Sent, r.Bytes));
            Elapsed = DateTime.Now - Started;
            Phase = r.Ok ? "Done" : "Failed";
        }
        catch (OperationCanceledException) { Elapsed = DateTime.Now - Started; Phase = "Cancelled"; }
        catch (Exception ex) { Message = Mask(ex.Message); Elapsed = DateTime.Now - Started; Phase = "Failed"; }
    }

    /// <summary>Called by the page's timer: speed over the time since the last call.</summary>
    public void Sample()
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastSample).TotalSeconds;
        if (dt < 0.4) return;
        var sent = Sent;
        Speed = IsRunning ? Math.Max(0, (sent - _lastSent) / dt) : 0;
        _lastSent = sent;
        _lastSample = now;
    }
}

internal static class UploadJobs
{
    public static readonly List<UploadJob> All = new();
    private static int _next;

    public static UploadJob Add(UploadOptions o)
    {
        var job = new UploadJob(++_next, o);
        All.Add(job);
        return job;
    }
}

/// <summary>
/// Upload File, in the style of HTTP Download: "New upload" on top, the uploads below as cards.
/// The target is Dropbox (one-time sign-in from the Dropbox menu) or any web address (POST form / PUT body).
/// </summary>
public sealed class UploadPage : ToolPage
{
    private readonly StackPanel _cardsHost = new() { Spacing = 8 };
    private readonly StackPanel _empty;
    private readonly List<UploadCard> _cards = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _dropboxState = new() { Opacity = 0.75, FontSize = 12 };
    private bool _busy;

    public UploadPage() : base("upload", "Upload File", "")
    {
        var dropbox = new DropDownButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new FontIcon { Glyph = "\uE753", FontSize = 14 }, new TextBlock { Text = "Dropbox" } },
            },
        };
        var menu = new MenuFlyout();
        var signIn = new MenuFlyoutItem { Text = "Sign in…", Icon = new FontIcon { Glyph = "\uE8D7" } };
        signIn.Click += (_, _) => _ = DropboxSignInAsync();
        var signOut = new MenuFlyoutItem { Text = "Sign out", Icon = new FontIcon { Glyph = "\uE7E8" } };
        signOut.Click += (_, _) => _ = DropboxSignOutAsync();
        menu.Items.Add(signIn);
        menu.Items.Add(signOut);
        dropbox.Flyout = menu;
        AutomationProperties.SetName(dropbox, "Dropbox");

        SetHeader("Upload File",
            "Send a file to Dropbox or to any web address (POST or PUT), with a progress bar.",
            ActionBtn("\uE710", "New upload", () => _ = NewUploadAsync(), accent: true),
            ActionBtn("\uE74D", "Clear finished", ClearFinished),
            dropbox);

        _empty = EmptyState("\uE898", "Click New upload above, or drop a file here");
        // dropping a file anywhere on the page opens "New upload" with that file already filled in
        AllowDrop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);     // a page with no background gets no drag events
        DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Upload this file";
        };
        Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<Windows.Storage.StorageFile>().FirstOrDefault();
                if (file == null) { Status.Text = "Drop a file (folders cannot be uploaded)."; return; }
                await NewUploadAsync(file.Path);
            }
            catch (Exception ex) { Status.Text = "Could not read the dropped file: " + ex.Message; }
            finally { deferral.Complete(); }
        };

        Body.Children.Add(_dropboxState);
        Body.Children.Add(_empty);
        Body.Children.Add(_cardsHost);

        _timer.Tick += (_, _) => { foreach (var c in _cards) c.Update(); };
        Loaded += (_, _) =>
        {
            foreach (var job in UploadJobs.All.Where(j => _cards.All(c => c.Job != j))) AddCard(job);
            RefreshEmpty();
            RefreshDropboxState();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();     // uploads keep running in the background
    }

    private void RefreshEmpty() => _empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshDropboxState()
        => _dropboxState.Text = DropboxStore.SignedIn ? "Dropbox: signed in" : "Dropbox: not signed in (use the Dropbox menu, then Sign in)";

    private void ClearFinished()
    {
        foreach (var c in _cards.Where(c => c.Job.Finished).ToList()) RemoveCard(c);
    }

    private void RemoveCard(UploadCard c)
    {
        if (!c.Job.Finished) c.Job.Cancel();
        UploadJobs.All.Remove(c.Job);
        _cards.Remove(c);
        _cardsHost.Children.Remove(c.Root);
        RefreshEmpty();
    }

    private void AddCard(UploadJob job)
    {
        var card = new UploadCard(job, RemoveCard, Copy);
        _cards.Insert(0, card);
        _cardsHost.Children.Insert(0, card.Root);
        card.Update();
        CursorHelper.ApplyHandCursorToButtons(card.Root);
        RefreshEmpty();
    }

    // ───────── Dropbox sign-in ─────────

    private async Task DropboxSignInAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var hasBuiltIn = DropboxStore.BuiltInAppKey.Length > 0;
            var key = new TextBox
            {
                Header = hasBuiltIn ? "Your own Dropbox app key (optional)" : "Dropbox app key",
                Text = DropboxStore.CustomAppKey,
                PlaceholderText = hasBuiltIn ? "leave empty to use ASD's Dropbox app" : "from your app in the Dropbox App Console",
                IsSpellCheckEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(key, "Dropbox app key");
            var dlg = MakeDialog("Sign in to Dropbox");
            dlg.Content = new StackPanel
            {
                Spacing = 10, MinWidth = 560,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
                        Text = (hasBuiltIn
                               ? "ASD opens your browser, you sign in to Dropbox and press Allow. "
                               : "No Dropbox app is built into this ASD yet. Do this once: in the Dropbox App Console (dropbox.com/developers/apps) create an app with Scoped access and the App folder. " +
                                 "In its Permissions tab tick files.content.write and sharing.write and press Submit. " +
                                 "In its Settings tab add  " + DropboxAuth.RedirectUri + "  under Redirect URIs. Copy its App key here. ") +
                               "The sign-in is saved encrypted in your Keys Store, and files go to the Apps folder of your own Dropbox, using your own space.",
                    },
                    key,
                },
            };
            dlg.PrimaryButtonText = "Open the browser";
            dlg.CloseButtonText = "Cancel";
            dlg.DefaultButton = ContentDialogButton.Primary;
            dlg.Opened += (_, _) => key.Focus(FocusState.Programmatic);
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            DropboxStore.CustomAppKey = key.Text;
            var appKey = DropboxStore.AppKey;
            if (appKey.Length == 0) { Status.Text = "Write the app key first."; return; }
            if (!KeyVault.Exists) { Status.Text = "Create your Keys Store first (open Keys Store): the Dropbox sign-in is kept there, encrypted."; return; }
            if (!await KeyUnlock.EnsureAsync(this, "save the Dropbox sign-in")) { Status.Text = "Cancelled: the Keys Store stayed locked."; return; }

            Status.Text = "Waiting for you to press Allow in the browser…";
            var token = await DropboxAuth.SignInAsync(appKey, CancellationToken.None);
            DropboxStore.SaveToken(token);
            Status.Text = "Dropbox is connected.";
        }
        catch (Exception ex) { Status.Text = "Dropbox sign-in failed: " + ex.Message; }
        finally { _busy = false; RefreshDropboxState(); }
    }

    private async Task DropboxSignOutAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!DropboxStore.SignedIn) { Status.Text = "Dropbox is not signed in."; return; }
            if (!await KeyUnlock.EnsureAsync(this, "remove the Dropbox sign-in")) return;
            DropboxStore.Forget();
            Status.Text = "Signed out of Dropbox (the saved sign-in was removed from the Keys Store).";
        }
        catch (Exception ex) { Status.Text = "Error: " + ex.Message; }
        finally { _busy = false; RefreshDropboxState(); }
    }

    // ───────── "New upload" ─────────

    private sealed class Form
    {
        public string File = "", Where = "", Headers = "";
        public int Target;
        public bool Overwrite, ShareLink = true, Insecure;
    }

    private async Task NewUploadAsync(string? presetFile = null)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var form = await AskAsync(presetFile);
            if (form != null) await StartAsync(form);
        }
        catch (Exception ex) { Fail(ex); }
        finally { _busy = false; }
    }

    private async Task<Form?> AskAsync(string? presetFile = null)
    {
        var file = Input("file", "File to upload (full path)", placeholder: @"C:\Users\you\Documents\report.zip", persist: false, multiline: false);
        AutomationProperties.SetName(file, "File to upload");
        if (!string.IsNullOrEmpty(presetFile)) file.Text = presetFile;
        var browse = Btn("Browse…", async () => { var p = await FilePick.AnyAsync(); if (p != null) file.Text = p; });
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        var fileRow = new Grid { ColumnSpacing = 8 };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browse, 1);
        fileRow.Children.Add(file);
        fileRow.Children.Add(browse);

        var target = Combo("Dropbox", "HTTP POST (multipart form, the field is called \"file\")", "HTTP PUT (the file is the request body)");
        target.Header = "Send it to";
        target.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(target, "Send it to");
        target.SelectedIndex = Math.Clamp(int.TryParse(SettingsStore.Get("upload.target", "0"), out var ti) ? ti : 0, 0, 2);

        var where = Input("where", "Dropbox folder or file path", placeholder: "/Reports/   (empty = the app folder)", persist: false, multiline: false);
        AutomationProperties.SetName(where, "Where to send it");
        where.Text = SettingsStore.Get("upload.where." + target.SelectedIndex, "");
        var headers = Input("uheaders", "Extra headers for HTTP (optional, one per line)", 70, "Authorization: Bearer {{key:My-Token}}", persist: false);
        AutomationProperties.SetName(headers, "Extra headers");

        var overwrite = Check("Replace a file with the same name (otherwise Dropbox adds “(1)”)", SettingsStore.Get("upload.overwrite", "off") == "on");
        var share = Check("Create a share link", SettingsStore.Get("upload.share", "on") == "on");
        var insecure = Check("Ignore TLS certificate errors", SettingsStore.Get("upload.insecure", "off") == "on");
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };

        var dropboxOptions = Row(overwrite, share);
        var httpOptions = Row(insecure);
        var lastWhere = new Dictionary<int, string>();
        var previous = target.SelectedIndex;
        void Sync()
        {
            lastWhere[previous] = where.Text;
            previous = target.SelectedIndex;
            var dropbox = target.SelectedIndex == 0;
            where.Header = dropbox ? "Dropbox folder or file path" : "URL (https://…)";
            where.PlaceholderText = dropbox ? "/Reports/   (empty = the app folder)" : "https://example.com/upload";
            where.Text = lastWhere.TryGetValue(previous, out var w) ? w : SettingsStore.Get("upload.where." + previous, "");
            headers.Visibility = dropbox ? Visibility.Collapsed : Visibility.Visible;
            dropboxOptions.Visibility = dropbox ? Visibility.Visible : Visibility.Collapsed;
            httpOptions.Visibility = dropbox ? Visibility.Collapsed : Visibility.Visible;
        }
        target.SelectionChanged += (_, _) => Sync();
        previous = target.SelectedIndex;
        // first fill without losing the saved text
        {
            var dropbox = target.SelectedIndex == 0;
            where.Header = dropbox ? "Dropbox folder or file path" : "URL (https://…)";
            where.PlaceholderText = dropbox ? "/Reports/   (empty = the app folder)" : "https://example.com/upload";
            headers.Visibility = dropbox ? Visibility.Collapsed : Visibility.Visible;
            dropboxOptions.Visibility = dropbox ? Visibility.Visible : Visibility.Collapsed;
            httpOptions.Visibility = dropbox ? Visibility.Collapsed : Visibility.Visible;
        }

        TextBox last = where;
        where.GotFocus += (_, _) => last = where;
        headers.GotFocus += (_, _) => last = headers;

        var content = new StackPanel
        {
            Spacing = 12, MinWidth = 620,
            Children =
            {
                fileRow, target, where, headers,
                KeyRefs.InsertButton(() => last),
                dropboxOptions, httpOptions, problem,
            },
        };

        var dlg = MakeDialog("New upload");
        dlg.Content = Scrollable(content);
        dlg.PrimaryButtonText = "Upload";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.PrimaryButtonClick += (_, a) =>
        {
            string? err = null;
            var path = file.Text.Trim().Trim('"');
            if (path.Length == 0) err = "Choose the file to upload.";
            else if (!System.IO.File.Exists(path)) err = $"This file does not exist: {path}";
            else if (target.SelectedIndex == 0 && !DropboxStore.SignedIn) err = "Dropbox is not signed in. Close this window and use the Dropbox menu, then Sign in.";
            else if (target.SelectedIndex != 0 && !(where.Text.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase) || where.Text.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase) || where.Text.Contains("{{")))
                err = "The address must start with http:// or https://";
            if (err != null) { problem.Text = err; problem.Visibility = Visibility.Visible; a.Cancel = true; }
        };
        dlg.Opened += (_, _) => { CursorHelper.ApplyHandCursorToButtons(content); file.Focus(FocusState.Programmatic); };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        var f = new Form
        {
            File = file.Text.Trim().Trim('"'), Where = where.Text.Trim(), Headers = headers.Text, Target = target.SelectedIndex,
            Overwrite = Is(overwrite), ShareLink = Is(share), Insecure = Is(insecure),
        };
        SettingsStore.SetMany(new[]
        {
            new KeyValuePair<string, string>("upload.target", f.Target.ToString()),
            new KeyValuePair<string, string>("upload.where." + f.Target, f.Where),
            new KeyValuePair<string, string>("upload.overwrite", f.Overwrite ? "on" : "off"),
            new KeyValuePair<string, string>("upload.share", f.ShareLink ? "on" : "off"),
            new KeyValuePair<string, string>("upload.insecure", f.Insecure ? "on" : "off"),
        });
        return f;
    }

    private ScrollViewer Scrollable(UIElement content)
    {
        var h = XamlRoot?.Size.Height ?? 800;
        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, h - 230),
            Padding = new Thickness(0, 0, 16, 0),
        };
    }

    private async Task StartAsync(Form f)
    {
        var opt = new UploadOptions
        {
            Target = f.Target switch { 1 => UploadTarget.Post, 2 => UploadTarget.Put, _ => UploadTarget.Dropbox },
            FilePath = f.File, Where = f.Where, Overwrite = f.Overwrite, ShareLink = f.ShareLink, Insecure = f.Insecure,
        };
        var headerLines = f.Target == 0 ? new List<string>() : f.Headers.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // {{key:Name}} placeholders (and the Dropbox sign-in) are read from the Keys Store now, after the master password
        var names = KeyRefs.Names(opt.Where, string.Join("\n", headerLines)).ToList();
        if (opt.Target == UploadTarget.Dropbox) names.Add(DropboxStore.RefName);
        var set = new KeyRefSet();
        if (names.Count > 0)
        {
            if (!await KeyUnlock.EnsureAsync(this, "read the keys this upload needs")) { Status.Text = "Cancelled: the Keys Store stayed locked."; return; }
            set = KeyRefs.Resolve(names);
            if (set.Missing.Count > 0) { Status.Text = "These keys are not in the Keys Store: " + string.Join(", ", set.Missing); return; }
        }

        opt.Where = KeyRefs.Replace(opt.Where, set);
        foreach (var line in headerLines)
        {
            var i = line.IndexOf(':');
            if (i <= 0) { Status.Text = $"The header \"{line}\" must look like  Name: value"; return; }
            opt.Headers.Add((line[..i].Trim(), KeyRefs.Replace(line[(i + 1)..].Trim(), set)));
        }

        var job = UploadJobs.Add(opt);
        job.Mask = KeyRefs.Masker(set);
        if (opt.Target == UploadTarget.Dropbox)
        {
            job.AppKey = DropboxStore.AppKey;
            job.Refresh = set.Values.TryGetValue(DropboxStore.RefName, out var rt) ? rt : null;
        }
        AddCard(job);
        job.Start();
        Status.Text = "";
    }

    // ───────── one upload card ─────────

    private sealed class UploadCard
    {
        public readonly UploadJob Job;
        public readonly Border Root;

        private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100 };
        private readonly TextBlock _summary = new() { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _problem = new() { FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) };
        private readonly Button _retry, _cancel, _copy, _remove;

        public UploadCard(UploadJob job, Action<UploadCard> onRemove, Action<string?> copy)
        {
            Job = job;
            _retry = Small("Retry");
            _retry.Click += (_, _) => Job.Start();
            _cancel = Small("Cancel");
            _cancel.Click += (_, _) => Job.Cancel();
            _copy = Small("Copy result");
            _copy.Click += (_, _) => copy(Job.Output);
            _remove = Small("Remove");
            _remove.Click += (_, _) => onRemove(this);
            AutomationProperties.SetName(_cancel, "Cancel upload");
            AutomationProperties.SetName(_remove, "Remove upload");

            var left = new StackPanel { Spacing = 6, Children = { _title, _bar, _summary, _problem } };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                Children = { _retry, _cancel, _copy, _remove },
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
        }

        private static Button Small(string text) => new() { Content = text, Padding = new Thickness(10, 4, 10, 4) };

        public void Update()
        {
            var j = Job;
            j.Sample();
            var sent = j.Sent;
            var total = j.Total;
            var known = total > 0;
            var where = j.Opt.Target switch
            {
                UploadTarget.Dropbox => "Dropbox " + (j.Opt.Where.Length > 0 ? j.Opt.Where : "/"),
                UploadTarget.Post => "POST " + j.Mask(j.Opt.Where),
                _ => "PUT " + j.Mask(j.Opt.Where),
            };

            _title.Text = $"{j.FileName}   ·   {j.Phase}";
            _bar.IsIndeterminate = j.Phase == "Sending" && !known;
            if (!_bar.IsIndeterminate) _bar.Value = known ? Math.Min(100, sent * 100.0 / total) : (j.Phase == "Done" ? 100 : 0);
            _bar.ShowError = j.Phase == "Failed";

            var text = $"{where}   ·   {SizeFmt.Bytes(sent)}" + (known ? $" of {SizeFmt.Bytes(total)}  ({sent * 100.0 / total:0}%)" : "");
            if (j.Phase == "Sending")
            {
                text += $"  ·  {SizeFmt.Speed(j.Speed)}";
                if (known && j.Speed > 1) text += $"  ·  about {SizeFmt.Duration(TimeSpan.FromSeconds(Math.Max(0, total - sent) / j.Speed))} left";
            }
            else if (j.Phase == "Done") text += $"  ·  {SizeFmt.Duration(j.Elapsed)}";
            if (j.Phase == "Done" && j.Message.Length > 0) text += "\n" + j.Message;
            _summary.Text = text;

            var err = j.Phase == "Failed" ? j.Message : "";
            _problem.Text = err;
            _problem.Visibility = err.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            _cancel.Visibility = j.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            _retry.Visibility = j.Phase is "Failed" or "Cancelled" ? Visibility.Visible : Visibility.Collapsed;
            _copy.Visibility = j.Phase == "Done" && j.Output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
