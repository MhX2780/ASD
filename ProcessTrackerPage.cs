using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace ASD;

public sealed class ProcessTrackerPage : ToolPage
{
    private readonly TextBox _path, _args, _live, _log;
    private readonly CheckBox _capture;
    private readonly Button _stop;
    private int _lastLog = -1;

    public ProcessTrackerPage() : base("track", "Process Tracker",
        "Launch a program (or attach to a running one) and follow it from start to exit — CPU, memory, threads, handles, child processes and console output.")
    {
        _path = Input("path", "Program to launch (.exe / .bat / .cmd)", placeholder: @"C:\Tools\MyApp.exe", multiline: false);
        _args = Input("args", "Arguments (optional)", multiline: false);
        _capture = Check("Capture console output (hides the console window)");
        _live = Output("Live", 215);
        _log = Output("Timeline (newest first)", 280);
        _stop = Btn("Stop tracking", () => ProcessTracker.Stop());

        var drop = new Border
        {
            Height = 56,
            AllowDrop = true,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new TextBlock { Text = "Drop a program here", Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        drop.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Copy;
        drop.Drop += async (_, e) =>
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile f) _path.Text = f.Path;
            }
            catch (Exception ex) { Fail(ex); }
        };

        Body.Children.Add(drop);
        Body.Children.Add(_path);
        Body.Children.Add(_args);
        Body.Children.Add(_capture);
        Body.Children.Add(Row(
            Btn("Browse…", () => Browse()),
            Btn("Start and track", StartTracking, true),
            Btn("Attach to a running process…", () => AttachClick()),
            _stop));
        Body.Children.Add(_live);
        Body.Children.Add(_log);
        Body.Children.Add(Row(
            Btn("Save report…", () => SaveReport()),
            Btn("Clear", () => ProcessTracker.Clear())));

        Loaded += (_, _) => { ProcessTracker.Changed += OnChanged; Refresh(true); };
        Unloaded += (_, _) => ProcessTracker.Changed -= OnChanged;
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(() => Refresh(false));

    private void Refresh(bool force)
    {
        _live.Text = ProcessTracker.LiveText();
        var (text, count) = ProcessTracker.LogText();
        if (force || count != _lastLog) { _log.Text = text; _lastLog = count; }
        _stop.IsEnabled = ProcessTracker.Running;
    }

    private void StartTracking()
    {
        try
        {
            ProcessTracker.Launch(_path.Text ?? "", _args.Text ?? "", Is(_capture));
            Status.Text = "";
            Refresh(true);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            Status.Text = "This program needs administrator rights. Run ASD as administrator to track it.";
        }
    }

    private async void Browse()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            foreach (var ext in new[] { ".exe", ".bat", ".cmd", ".com" }) picker.FileTypeFilter.Add(ext);
            var f = await picker.PickSingleFileAsync();
            if (f != null) _path.Text = f.Path;
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void AttachClick()
    {
        try
        {
            var all = Process.GetProcesses();
            var procs = all.Select(p =>
            {
                string title = "";
                try { title = p.MainWindowTitle; } catch { }
                return (Id: p.Id, Name: p.ProcessName, Title: title);
            }).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var p in all) p.Dispose();

            var box = new TextBox { PlaceholderText = "Filter by name, window title or PID…", IsSpellCheckEnabled = false };
            var list = new ListView { MaxHeight = 340, SelectionMode = ListViewSelectionMode.Single };
            var shown = procs;

            void Filter()
            {
                var q = (box.Text ?? "").Trim();
                shown = q.Length == 0 ? procs : procs.Where(p =>
                    p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.ToString().Contains(q)).ToList();
                list.ItemsSource = shown.Select(p => $"{p.Name}   ·   PID {p.Id}" + (p.Title.Length > 0 ? $"   ·   {p.Title}" : "")).ToList();
                if (shown.Count > 0) list.SelectedIndex = 0;
            }

            Filter();
            box.TextChanged += (_, _) => Filter();

            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Attach to a running process",
                PrimaryButtonText = "Track it",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel { Spacing = 8, Width = 520, Children = { box, list } },
            };
            dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);

            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary && list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count)
            {
                ProcessTracker.Attach(shown[list.SelectedIndex].Id);
                Status.Text = "";
                Refresh(true);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void SaveReport()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "process-report" };
            picker.FileTypeChoices.Add("Text report", new List<string> { ".txt" });
            picker.FileTypeChoices.Add("CSV samples", new List<string> { ".csv" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            var text = file.FileType.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ProcessTracker.CsvText() : ProcessTracker.ReportText();
            await FileIO.WriteTextAsync(file, text);
            Status.Text = "Saved ✔  " + file.Path;
        }
        catch (Exception ex) { Fail(ex); }
    }
}
