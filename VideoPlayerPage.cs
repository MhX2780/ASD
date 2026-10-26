using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;

namespace ASD;

/// <summary>
/// Video Player, built as a 3-step wizard:
///   1. Clip type  : "MP4 only" (video + sound in one file) or "Separate" (MP4 video + MP3 audio) → Next
///   2. Quality &amp; Timeout : Auto / 360p … 1080p and a network timeout                          → Next
///   3. View the video : player with a Play / Pause toggle button, seek bar and volume.
/// Sources can be local files or http(s) links. Quality applies to adaptive streams (HLS .m3u8 /
/// DASH .mpd); a plain MP4 has a single quality and is always played as-is.
/// </summary>
public sealed class VideoPlayerPage : ToolPage
{
    private static readonly HttpClient Http = new();
    private static readonly string[] QualityLabels = { "Auto", "360p", "480p", "720p", "1080p" };
    private static readonly int[] QualityHeights = { 0, 360, 480, 720, 1080 };
    private static readonly string[] TimeoutLabels = { "5 seconds", "10 seconds", "15 seconds", "30 seconds", "60 seconds", "120 seconds" };
    private static readonly int[] TimeoutSeconds = { 5, 10, 15, 30, 60, 120 };

    // ── step panels ──
    private readonly StackPanel _step1 = new() { Spacing = 12 };
    private readonly StackPanel _step2 = new() { Spacing = 12 };
    private readonly StackPanel _step3 = new() { Spacing = 12 };
    private readonly TextBlock _stepTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };

    // ── step 1 ──
    private readonly RadioButton _typeMp4, _typeSeparate;
    private readonly TextBox _mp4, _mp3;
    private readonly StackPanel _mp3Row;

    // ── step 2 ──
    private readonly List<RadioButton> _quality = new();
    private readonly ComboBox _timeout;

    // ── step 3 ──
    private readonly MediaPlayerElement _element = new()
    {
        AreTransportControlsEnabled = false,
        AutoPlay = false,
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        Height = 420,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly ToggleButton _playToggle = new() { MinWidth = 110 };
    private readonly FontIcon _playIcon = new() { Glyph = "\uE768", FontSize = 16 };
    private readonly TextBlock _playText = new() { Text = "Play" };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.1, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 80, Width = 130 };
    private readonly TextBlock _time = new() { Text = "0:00 / 0:00", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };

    // ── playback state ──
    private MediaPlayer? _video, _audio;
    private AdaptiveMediaSource? _adaptive;
    private bool _separate, _updatingSeek, _opening;
    private int _openId;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _watch = new();
    private string _watchReason = "";

    public VideoPlayerPage() : base("video", "Video Player",
        "Play MP4 clips, or an MP4 video with a separate MP3 audio track, from a file or a link.")
    {
        // ───────── Step 1: clip type ─────────
        _typeMp4 = new RadioButton { Content = "MP4 only  —  video and sound are in one file", GroupName = "vp_type", IsChecked = true };
        _typeSeparate = new RadioButton { Content = "Separate  —  MP4 video + MP3 audio (two files)", GroupName = "vp_type" };
        _mp4 = Input("mp4", "MP4 video — file path or link", placeholder: @"C:\Videos\clip.mp4   or   https://…/clip.mp4", multiline: false);
        _mp3 = Input("mp3", "MP3 audio — file path or link", placeholder: @"C:\Music\sound.mp3   or   https://…/sound.mp3", multiline: false);
        _mp3Row = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Children = { Grid2(_mp3, Btn("Browse…", () => _ = BrowseAsync(_mp3, ".mp3", ".wav", ".m4a", ".aac"))) } };

        _typeMp4.Checked += (_, _) => _mp3Row.Visibility = Visibility.Collapsed;
        _typeSeparate.Checked += (_, _) =>
        {
            _mp3Row.Visibility = Visibility.Visible;
            CursorHelper.ApplyHandCursorToButtons(this);
        };

        _step1.Children.Add(Label("What kind of clip is it?"));
        _step1.Children.Add(_typeMp4);
        _step1.Children.Add(_typeSeparate);
        _step1.Children.Add(Grid2(_mp4, Btn("Browse…", () => _ = BrowseAsync(_mp4, ".mp4", ".m4v", ".mov", ".mkv", ".webm"))));
        _step1.Children.Add(_mp3Row);
        _step1.Children.Add(Row(Btn("Next", GoStep2, accent: true)));

        // ───────── Step 2: quality & timeout ─────────
        var savedQuality = Math.Clamp(int.TryParse(SettingsStore.Get("video.quality", "0"), out var q) ? q : 0, 0, QualityLabels.Length - 1);
        var qualityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (int i = 0; i < QualityLabels.Length; i++)
        {
            var rb = new RadioButton { Content = QualityLabels[i], GroupName = "vp_quality", IsChecked = i == savedQuality, MinWidth = 90 };
            _quality.Add(rb);
            qualityRow.Children.Add(rb);
        }
        _timeout = Combo(TimeoutLabels);
        _timeout.SelectedIndex = Math.Clamp(int.TryParse(SettingsStore.Get("video.timeout", "2"), out var t) ? t : 2, 0, TimeoutLabels.Length - 1);

        _step2.Children.Add(Label("Quality"));
        _step2.Children.Add(qualityRow);
        _step2.Children.Add(new TextBlock
        {
            Text = "Auto picks the best resolution for your internet speed. Fixed qualities apply to adaptive streams (HLS .m3u8 / DASH .mpd). A normal MP4 file has one quality and plays as it is.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        _step2.Children.Add(Label("Timeout"));
        _step2.Children.Add(Row(_timeout));
        _step2.Children.Add(new TextBlock
        {
            Text = "How long to wait for a link to answer, and for the video to open or recover from buffering, before giving up.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        _step2.Children.Add(Row(Btn("Back", () => ShowStep(1)), Btn("Next", GoStep3, accent: true)));

        // ───────── Step 3: view ─────────
        _playToggle.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _playIcon, _playText } };
        _playToggle.Click += (_, _) => { if (_playToggle.IsChecked == true) Play(); else Pause(); SyncToggle(); };
        _seek.ValueChanged += (_, e) => { if (!_updatingSeek) SeekTo(e.NewValue); };
        _volume.ValueChanged += (_, e) => ApplyVolume();

        var controls = new Grid { ColumnSpacing = 12 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_seek, 1); Grid.SetColumn(_time, 2); Grid.SetColumn(_volume, 3);
        controls.Children.Add(_playToggle); controls.Children.Add(_seek); controls.Children.Add(_time); controls.Children.Add(_volume);

        _step3.Children.Add(_element);
        _step3.Children.Add(controls);
        _step3.Children.Add(Row(Btn("Back", () => { StopPlayback(); ShowStep(2); }), Btn("New clip", () => { StopPlayback(); ShowStep(1); })));

        Body.Children.Add(_stepTitle);
        Body.Children.Add(_step1);
        Body.Children.Add(_step2);
        Body.Children.Add(_step3);
        ShowStep(1);

        _tick.Tick += (_, _) => OnTick();
        _watch.Tick += (_, _) => OnWatchTimeout();
        Unloaded += (_, _) => { _tick.Stop(); _watch.Stop(); StopPlayback(); };
    }

    // ═════════════════ wizard ═════════════════
    private void ShowStep(int step)
    {
        _step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        _step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        _step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        _stepTitle.Text = step switch
        {
            1 => "Step 1 of 3  ·  Clip type",
            2 => "Step 2 of 3  ·  Quality & Timeout",
            _ => "Step 3 of 3  ·  View the video",
        };
        Status.Text = "";
        CursorHelper.ApplyHandCursorToButtons(this);   // hand cursor on the buttons / radio buttons of the new step
    }

    private void GoStep2()
    {
        if (string.IsNullOrWhiteSpace(_mp4.Text)) { Status.Text = "Enter the MP4 video file or link first."; return; }
        if (_typeSeparate.IsChecked == true && string.IsNullOrWhiteSpace(_mp3.Text)) { Status.Text = "Enter the MP3 audio file or link first."; return; }
        ShowStep(2);
    }

    private void GoStep3()
    {
        SettingsStore.Set("video.quality", SelectedQualityIndex().ToString());
        SettingsStore.Set("video.timeout", Math.Max(0, _timeout.SelectedIndex).ToString());
        ShowStep(3);
        _ = OpenAsync();
    }

    private int SelectedQualityIndex() => Math.Max(0, _quality.FindIndex(r => r.IsChecked == true));

    private async Task BrowseAsync(TextBox target, params string[] extensions)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var e in extensions) picker.FileTypeFilter.Add(e);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file != null) target.Text = file.Path;
        }
        catch (Exception ex) { Fail(ex); }
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

    // ═════════════════ opening ═════════════════
    private async Task OpenAsync()
    {
        StopPlayback();
        var id = ++_openId;
        _opening = true;
        _separate = _typeSeparate.IsChecked == true;
        var timeout = TimeoutSeconds[Math.Max(0, _timeout.SelectedIndex)];
        var qualityIndex = SelectedQualityIndex();
        Status.Text = "Loading…";

        try
        {
            var (videoSource, adaptive) = await BuildSourceAsync(_mp4.Text.Trim(), timeout, allowAdaptive: true);
            MediaSource? audioSource = null;
            if (_separate) (audioSource, _) = await BuildSourceAsync(_mp3.Text.Trim(), timeout, allowAdaptive: false);
            if (id != _openId) return;   // the user went back / opened something else meanwhile

            _adaptive = adaptive;
            var qualityNote = ApplyQuality(adaptive, qualityIndex);

            var video = new MediaPlayer { AutoPlay = false, IsMuted = _separate };
            video.MediaOpened += (_, _) => Ui(() => OnOpened(id, qualityNote));
            video.MediaFailed += (_, e) => Ui(() => OnFailed(id, "Video: " + e.ErrorMessage));
            video.MediaEnded += (_, _) => Ui(OnEnded);
            video.PlaybackSession.BufferingStarted += (_, _) => Ui(() => OnBuffering(true));
            video.PlaybackSession.BufferingEnded += (_, _) => Ui(() => OnBuffering(false));
            _video = video;

            if (audioSource != null)
            {
                var audio = new MediaPlayer { AutoPlay = false };
                audio.MediaFailed += (_, e) => Ui(() => OnFailed(id, "Audio: " + e.ErrorMessage));
                audio.Source = audioSource;
                _audio = audio;
            }

            _element.SetMediaPlayer(video);
            ArmWatch(timeout, $"The video did not open within {timeout} seconds.");
            video.Source = videoSource;
            ApplyVolume();
            _tick.Start();
        }
        catch (Exception ex)
        {
            if (id == _openId) OnFailed(id, ex.Message);
        }
        finally
        {
            if (id == _openId) _opening = false;
        }
    }

    /// <summary>Local file → StorageFile source; http(s) link → reachability check (with timeout), then plain or adaptive source.</summary>
    private async Task<(MediaSource Source, AdaptiveMediaSource? Adaptive)> BuildSourceAsync(string text, int timeoutSeconds, bool allowAdaptive)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            var path = text.Trim('"');
            if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            return (MediaSource.CreateFromStorageFile(file), null);
        }

        await CheckReachableAsync(uri, timeoutSeconds);

        var lower = uri.AbsolutePath.ToLowerInvariant();
        if (allowAdaptive && (lower.EndsWith(".m3u8") || lower.EndsWith(".mpd")))
        {
            var create = AdaptiveMediaSource.CreateFromUriAsync(uri).AsTask();
            if (await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != create)
                throw new TimeoutException($"The stream did not answer within {timeoutSeconds} seconds.");
            var result = await create;
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                throw new InvalidOperationException("Cannot open the stream: " + result.Status);
            return (MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource), result.MediaSource);
        }
        return (MediaSource.CreateFromUri(uri), null);
    }

    private static async Task CheckReachableAsync(Uri uri, int timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode != 416)
                throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The link did not answer within {timeoutSeconds} seconds (timeout).");
        }
    }

    /// <summary>Fixes the stream to the chosen resolution, or leaves bandwidth-based Auto. Returns a note for the status line.</summary>
    private static string ApplyQuality(AdaptiveMediaSource? ams, int qualityIndex)
    {
        var height = QualityHeights[qualityIndex];
        if (ams == null)
            return height == 0 ? "Quality: Auto (single file, original quality)" : $"Quality: {QualityLabels[qualityIndex]} is only available for adaptive streams, playing the file in its original quality";
        if (height == 0 || ams.AvailableBitrates.Count == 0)
            return "Quality: Auto (follows your internet speed)";

        // Typical bitrate ceilings per resolution; take the highest available bitrate that fits.
        uint ceiling = height switch { 360 => 1_200_000, 480 => 2_500_000, 720 => 5_000_000, _ => 8_500_000 };
        var sorted = ams.AvailableBitrates.OrderBy(b => b).ToList();
        var pick = sorted.Where(b => b <= ceiling).DefaultIfEmpty(sorted[0]).Max();
        ams.InitialBitrate = pick;
        ams.DesiredMinBitrate = pick;
        ams.DesiredMaxBitrate = pick;
        return $"Quality: {QualityLabels[qualityIndex]} (~{pick / 1000} kbps)";
    }

    // ═════════════════ playback events (UI thread) ═════════════════
    private void Ui(Action a) => DispatcherQueue.TryEnqueue(() => { try { a(); } catch { /* page already gone */ } });

    private void OnOpened(int id, string qualityNote)
    {
        if (id != _openId || _video == null) return;
        _watch.Stop();
        var seconds = _video.PlaybackSession.NaturalDuration.TotalSeconds;
        _updatingSeek = true;
        _seek.Maximum = seconds > 0 ? seconds : 1;
        _seek.Value = 0;
        _seek.IsEnabled = seconds > 0;
        _updatingSeek = false;
        Status.Text = $"Ready — press Play.   {qualityNote}";
        CursorHelper.ApplyHandCursorToButtons(this);
    }

    private void OnFailed(int id, string message)
    {
        if (id != _openId) return;
        _watch.Stop();
        _tick.Stop();
        _playToggle.IsChecked = false;
        SyncToggle();
        Status.Text = "Error: " + message;
    }

    private void OnEnded()
    {
        _audio?.Pause();
        _playToggle.IsChecked = false;
        SyncToggle();
    }

    private void OnBuffering(bool started)
    {
        if (started)
        {
            _audio?.Pause();
            ArmWatch(TimeoutSeconds[Math.Max(0, _timeout.SelectedIndex)], "The video stopped to buffer for too long.");
            Status.Text = "Buffering…";
        }
        else
        {
            _watch.Stop();
            if (_playToggle.IsChecked == true) _audio?.Play();
            Status.Text = "";
        }
    }

    private void ArmWatch(int seconds, string reason)
    {
        _watchReason = reason;
        _watch.Stop();
        _watch.Interval = TimeSpan.FromSeconds(seconds);
        _watch.Start();
    }

    private void OnWatchTimeout()
    {
        _watch.Stop();
        _video?.Pause();
        _audio?.Pause();
        _playToggle.IsChecked = false;
        SyncToggle();
        Status.Text = "Timeout: " + _watchReason;
    }

    // ═════════════════ controls ═════════════════
    private void Play()
    {
        if (_video == null) { _playToggle.IsChecked = false; return; }
        _video.Play();
        _audio?.Play();
    }

    private void Pause()
    {
        _video?.Pause();
        _audio?.Pause();
    }

    /// <summary>One toggle button for both actions: checked = playing (shows Pause), unchecked = paused (shows Play).</summary>
    private void SyncToggle()
    {
        var playing = _playToggle.IsChecked == true;
        _playIcon.Glyph = playing ? "\uE769" : "\uE768";
        _playText.Text = playing ? "Pause" : "Play";
    }

    private void SeekTo(double seconds)
    {
        var position = TimeSpan.FromSeconds(seconds);
        if (_video != null) _video.PlaybackSession.Position = position;
        if (_audio != null) _audio.PlaybackSession.Position = position;
    }

    private void ApplyVolume()
    {
        var v = _volume.Value / 100.0;
        if (_separate && _audio != null) _audio.Volume = v;
        else if (_video != null) _video.Volume = v;
    }

    private void OnTick()
    {
        if (_video == null) return;
        var pos = _video.PlaybackSession.Position;
        var total = _video.PlaybackSession.NaturalDuration;

        // keep the separate audio track locked to the video
        if (_audio != null && _playToggle.IsChecked == true && Math.Abs((_audio.PlaybackSession.Position - pos).TotalSeconds) > 0.3)
            _audio.PlaybackSession.Position = pos;

        _updatingSeek = true;
        if (_seek.IsEnabled) _seek.Value = Math.Min(pos.TotalSeconds, _seek.Maximum);
        _updatingSeek = false;
        _time.Text = $"{Fmt(pos)} / {Fmt(total)}";
    }

    private static string Fmt(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void StopPlayback()
    {
        _openId++;
        _watch.Stop();
        _tick.Stop();
        try { _element.SetMediaPlayer(null); } catch { }
        try { _video?.Pause(); _video?.Dispose(); } catch { }
        try { _audio?.Pause(); _audio?.Dispose(); } catch { }
        _video = null; _audio = null; _adaptive = null;
        _playToggle.IsChecked = false;
        SyncToggle();
        _updatingSeek = true;
        _seek.Value = 0; _seek.IsEnabled = false;
        _updatingSeek = false;
        _time.Text = "0:00 / 0:00";
    }
}
