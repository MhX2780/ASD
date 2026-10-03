using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace ASD;

/// <summary>
/// Shown instead of the main window when a file named "cd-r.txt" sits next to ASD.exe.
/// That file marks a read-only disc (CD-R / DVD): ASD can't update itself or save anything there, so it
/// must first be copied to a folder chosen by the user. The copy is done here, by the app itself
/// (reading its own files while running is allowed), then the copy is started and this one exits.
/// The marker file is NOT copied, so the installed copy starts normally.
/// </summary>
public sealed partial class InstallWindow : Window
{
    public const string MarkerFileName = "cd-r.txt";

    /// <summary>True when cd-r.txt exists in the folder ASD runs from.</summary>
    public static bool IsDiscCopy => File.Exists(Path.Combine(AppContext.BaseDirectory, MarkerFileName));

    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    public InstallWindow()
    {
        InitializeComponent();
        AppWindow.Title = "Install ASD";

        RootGrid.RequestedTheme = SettingsStore.Get("ThemeMode", "auto") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        SetupWindowFrame();

        // Same cursors as the main window (the main window is never created in this mode)
        try
        {
            CursorHelper.Initialize(RootGrid, Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors"));
            CursorHelper.SetDarkTheme(RootGrid.ActualTheme == ElementTheme.Dark);
            CursorHelper.HookWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch { /* cosmetic only */ }
        RootGrid.ActualThemeChanged += (_, _) => CursorHelper.SetDarkTheme(RootGrid.ActualTheme == ElementTheme.Dark);

        RootGrid.Loaded += (_, _) =>
        {
            CursorHelper.ApplyPointer(RootGrid);
            CursorHelper.ApplyHandCursorToButtons(RootGrid);
        };

        Closed += (_, _) => _cts.Cancel();

        Show(Icon.Folder, "Install ASD",
            "This copy of ASD is running from a CD-R / read-only disc. Choose a folder: ASD will be copied there and started from the new location.",
            choose: true);
    }

    // ───────── window frame ─────────

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetupWindowFrame()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                    AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            }
            catch { /* cosmetic only */ }
            RootGrid.ActualThemeChanged += (_, _) => WindowChrome.ApplyCaptionColors(AppWindow, RootGrid.ActualTheme);
            WindowChrome.ApplyCaptionColors(AppWindow, RootGrid.ActualTheme);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            int w = (int)(520 * scale), h = (int)(460 * scale);
            AppWindow.Resize(new SizeInt32(w, h));

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));

            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsResizable = false;
                p.IsMaximizable = false;
            }
        }
        catch { /* cosmetic only */ }
    }

    // ───────── the install flow ─────────

    private async void OnChooseClick(object sender, RoutedEventArgs e) => await ChooseAndInstallAsync();

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private async Task ChooseAndInstallAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 1. Let the user pick the destination
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;   // cancelled: nothing changes

            var source = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var target = Path.Combine(folder.Path, "ASD");

            // 2. Check the destination before copying anything
            var problem = await Task.Run(() => CheckTarget(source, target));
            if (problem != null)
            {
                Show(Icon.Error, "Can't install here", problem, choose: true, chooseText: "Choose another folder…");
                return;
            }

            // 3. Copy the whole application folder
            Show(Icon.Ring, "Copying ASD…", $"To: {target}", bar: true, barIndeterminate: true, exit: false);
            var progress = new Progress<(long Done, long Total)>(OnCopyProgress);
            await Task.Run(() => CopyApp(source, target, progress, _cts.Token), _cts.Token);

            // 4. Start the installed copy and close this one
            Show(Icon.Success, "Done", $"ASD was copied to {target}. Starting it…", bar: true, barIndeterminate: true, exit: false);
            await Task.Delay(700, _cts.Token);
            var exeName = Path.GetFileName(Environment.ProcessPath ?? "ASD.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(target, exeName),
                WorkingDirectory = target,
                UseShellExecute = false,
            });
            Application.Current.Exit();
        }
        catch (OperationCanceledException)
        {
            // window closed while copying
        }
        catch (Exception ex)
        {
            Show(Icon.Error, "Install failed", ex.Message, choose: true, chooseText: "Choose another folder…");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Returns a message when the target can't be used, or null when it's fine.</summary>
    private static string? CheckTarget(string source, string target)
    {
        var src = Path.GetFullPath(source).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var dst = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (dst.StartsWith(src, StringComparison.OrdinalIgnoreCase))
            return "The destination can't be inside the folder ASD is running from.";

        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, $".asd-write-test-{Guid.NewGuid():N}.tmp");
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            fs.WriteByte(0);
        }
        catch
        {
            return "That folder is not writable. Choose another folder (not a CD/DVD).";
        }

        try
        {
            long need = ListFiles(source).Sum(f => new FileInfo(f).Length);
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\") &&
                new DriveInfo(root).AvailableFreeSpace < need + 50L * 1024 * 1024)
                return $"Not enough free space (ASD needs about {need / 1048576.0:0} MB).";
        }
        catch { /* can't tell (network path ...): let the copy decide */ }

        return null;
    }

    private static string MarkerPath(string source) => Path.Combine(source, MarkerFileName);

    /// <summary>All files of the application folder, except the cd-r.txt marker.</summary>
    private static List<string> ListFiles(string root)
    {
        var marker = Path.GetFullPath(MarkerPath(root));
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                    if (!string.Equals(Path.GetFullPath(f), marker, StringComparison.OrdinalIgnoreCase))
                        result.Add(f);

                foreach (var d in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || name.StartsWith('$')) continue;
                    pending.Push(d);
                }
            }
            catch (UnauthorizedAccessException) { /* skip folders we can't read */ }
            catch (IOException) { }
        }
        return result;
    }

    private static void CopyApp(string source, string target, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        var files = ListFiles(source);
        long total = files.Sum(f => new FileInfo(f).Length);
        long done = 0;
        var buffer = new byte[1024 * 1024];
        var clock = Stopwatch.StartNew();
        long lastReport = -1000;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            // Open for reading only and let everyone else keep their handles: ASD.exe and the DLLs are in use
            // by this very process, and Windows only blocks WRITING to a running file, never reading it.
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    if (clock.ElapsedMilliseconds - lastReport >= 100)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        progress.Report((done, total));
                    }
                }
            }

            // Files read from a disc are read-only: the copy must not be (updates overwrite these files)
            File.SetAttributes(dest, FileAttributes.Normal);
        }
        progress.Report((total, total));

        var exeName = Path.GetFileName(Environment.ProcessPath ?? "ASD.exe");
        if (!File.Exists(Path.Combine(target, exeName)))
            throw new FileNotFoundException($"{exeName} is missing from the copy.");
    }

    private void OnCopyProgress((long Done, long Total) p)
    {
        Bar.IsIndeterminate = false;
        Bar.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
        StatsText.Text = $"{p.Done / 1048576.0:0.0} MB / {p.Total / 1048576.0:0.0} MB  ·  {Bar.Value:0}%";
        StatsText.Visibility = Visibility.Visible;
    }

    // ───────── view helper ─────────

    private enum Icon { Folder, Ring, Success, Error }

    private void Show(Icon icon, string title, string detail, bool bar = false, bool barIndeterminate = false,
        bool choose = false, string chooseText = "Choose folder…", bool exit = true)
    {
        Ring.IsActive = icon == Icon.Ring;
        Ring.Visibility = icon == Icon.Ring ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = icon == Icon.Ring ? Visibility.Collapsed : Visibility.Visible;

        string glyph;
        int r, g, b;
        switch (icon)
        {
            case Icon.Success: glyph = ""; (r, g, b) = (0x1E, 0xA8, 0x4B); break;   // green check
            case Icon.Error:   glyph = ""; (r, g, b) = (0xE0, 0x4A, 0x3C); break;   // red warning
            default:           glyph = ""; (r, g, b) = (0x3B, 0x8E, 0xEA); break;   // blue folder
        }
        StatusIcon.Glyph = glyph;
        StatusIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b));

        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Bar.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        Bar.IsIndeterminate = barIndeterminate;
        if (bar && barIndeterminate) Bar.Value = 0;
        StatsText.Visibility = Visibility.Collapsed;

        ChooseButton.Content = chooseText;
        ChooseButton.Visibility = choose ? Visibility.Visible : Visibility.Collapsed;
        ExitButton.Visibility = exit ? Visibility.Visible : Visibility.Collapsed;
        CursorHelper.ApplyHandCursorToButtons(RootGrid);
    }
}
