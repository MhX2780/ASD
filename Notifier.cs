using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ASD;

internal enum NoteKind { Success, Error, Warning, Info }

/// <param name="Tool">Tag of the tool a click on the notification opens (e.g. "httpdl").</param>
/// <param name="KeyToken">Set for key-expiry notifications: "id|date|soon" or "id|date|expired". Dismissing one remembers it.</param>
internal sealed record NoteItem(DateTime Time, NoteKind Kind, string Title, string Subtitle, string Tool, string? KeyToken = null, string? ToastTitle = null, string? ToastText = null);

/// <summary>
/// The app's notification center. Tools call <see cref="Post"/> from any thread when something long finishes
/// (a download, a slow request). The bell in the title bar shows the unread count and its dialog lists them.
/// When the ASD window is not the active window (the person is in another program, or ASD is minimized) the same
/// notification is also shown as a Windows notification; clicking it brings ASD forward on that tool.
/// </summary>
internal static class Notifier
{
    public static readonly List<NoteItem> Items = new();    // newest first; only touched on the UI thread
    public static int Unread { get; private set; }
    public static event Action? Changed;                     // raised on the UI thread
    public static bool WindowActive { get; set; } = true;

    private static DispatcherQueue? _ui;
    private static Func<string?> _currentTool = () => null;
    private static bool _toasts;

    private static string LogPath => AppPaths.File("notifications.log");

    public static void Init(DispatcherQueue ui, Action<string> openTool, Action bringToFront, Func<string?> currentTool)
    {
        _ui = ui;
        _currentTool = currentTool;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += (_, args) =>
            {
                args.Arguments.TryGetValue("tool", out var tool);
                args.Arguments.TryGetValue("action", out var action);
                args.Arguments.TryGetValue("key", out var token);
                ui.TryEnqueue(() =>
                {
                    if (action == "dismiss") { if (!string.IsNullOrEmpty(token)) DismissKey(token); return; }   // silent: no window
                    bringToFront();
                    if (!string.IsNullOrEmpty(tool)) openTool(tool);
                });
            };
            manager.Register();
            _toasts = true;
            Log("Windows notifications registered.");

            // ASD was not running and a notification click started it
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs clicked)
            {
                clicked.Arguments.TryGetValue("action", out var startAction);
                clicked.Arguments.TryGetValue("key", out var startToken);
                if (startAction == "dismiss" && !string.IsNullOrEmpty(startToken))
                {
                    // Dismiss pressed while ASD was closed: Windows had to start it. Save the choice and close again.
                    DismissKey(startToken);
                    ui.TryEnqueue(() => App.MainWindow?.Close());
                }
                else if (clicked.Arguments.TryGetValue("tool", out var first) && !string.IsNullOrEmpty(first))
                    ui.TryEnqueue(() => openTool(first));
            }
        }
        catch (Exception ex)
        {
            _toasts = false;
            Log("Windows notifications are not available: " + ex.Message);
        }
    }

    public static void Shutdown()
    {
        try { if (_toasts) AppNotificationManager.Default.Unregister(); } catch { }
    }

    /// <summary>Adds a notification (any thread). Skipped when the person is looking at that very tool.</summary>
    public static void Post(NoteKind kind, string title, string subtitle, string tool,
                            string? keyToken = null, string? toastTitle = null, string? toastText = null)
    {
        var ui = _ui;
        if (ui == null) return;
        if (SettingsStore.Get("notify.enabled", "on") == "off") return;
        ui.TryEnqueue(() =>
        {
            // a finished download / request is not announced while the person looks at that very tool; key warnings always are
            if (keyToken == null && WindowActive && _currentTool() == tool)
            {
                Log($"{kind}: {title} | {subtitle} | skipped (already on that tool)");
                return;
            }
            var item = new NoteItem(DateTime.Now, kind, title, subtitle, tool, keyToken, toastTitle, toastText);
            Items.Insert(0, item);
            if (Items.Count > 50) Items.RemoveAt(Items.Count - 1);
            Unread++;
            var toast = WindowActive ? "not needed (window active)"
                      : SettingsStore.Get("notify.windows", "on") == "off" ? "off in Settings"
                      : ShowToast(item);
            Log($"{kind}: {title} | {subtitle} | toast: {toast}");
            Changed?.Invoke();
        });
    }

    private static string ShowToast(NoteItem n)
    {
        if (!_toasts) return "unavailable";
        try
        {
            var mark = n.Kind switch { NoteKind.Success => "✔ ", NoteKind.Error => "✘ ", NoteKind.Warning => "⚠ ", _ => "" };
            var builder = new AppNotificationBuilder()
                .AddArgument("tool", n.Tool)
                .AddText(mark + (n.ToastTitle ?? n.Title))
                .AddText(n.ToastText ?? n.Subtitle);
            if (n.KeyToken != null)
            {
                builder.AddButton(new AppNotificationButton("View").AddArgument("action", "view").AddArgument("tool", n.Tool));
                builder.AddButton(new AppNotificationButton("Dismiss").AddArgument("action", "dismiss").AddArgument("key", n.KeyToken));
            }
            AppNotificationManager.Default.Show(builder.BuildNotification());
            return "shown";
        }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }

    public static void MarkRead() { if (Unread == 0) return; Unread = 0; Changed?.Invoke(); }

    public static void Clear()
    {
        foreach (var n in Items.Where(i => i.KeyToken != null)) DismissKey(n.KeyToken!);
        Items.Clear(); Unread = 0; Changed?.Invoke();
    }

    /// <summary>Removes one notification. A key warning that is removed stays away (until the key's date changes or it expires).</summary>
    public static void Remove(NoteItem n)
    {
        if (n.KeyToken != null) DismissKey(n.KeyToken);
        if (Items.Remove(n)) Changed?.Invoke();
    }

    // ───────── key warnings the person dismissed ─────────
    private const string DismissedSetting = "keys.dismissed";

    public static HashSet<string> Dismissed()
        => SettingsStore.Get(DismissedSetting, "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void DismissKey(string token)
    {
        var all = Dismissed();
        if (!all.Add(token)) return;
        SettingsStore.Set(DismissedSetting, string.Join(";", all.TakeLast(300)));
    }

    /// <summary>A small text log (%LocalAppData%\ASD\notifications.log) so it can be checked what was posted and whether Windows showed it.</summary>
    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 100_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { /* diagnostics only */ }
    }
}
