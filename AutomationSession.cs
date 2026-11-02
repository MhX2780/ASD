using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ASD;

/// <summary>
/// The Automation editor's document and its run, kept outside the page so that
///  - the automation is still there (and a run keeps going) when you open another tool and come back,
///  - the text is kept as a draft in the ASD data folder and restored at the next start.
/// Everything here runs on the UI thread, except Log (any thread, queued).
/// </summary>
internal static class AutomationSession
{
    private static DispatcherQueue? _ui;
    private static bool _inited;

    // ── the document ──
    public static AutoFile Doc { get; private set; } = new();
    /// <summary>True when the text has errors: Doc is empty then and RawText holds what must be fixed.</summary>
    public static bool Invalid { get; private set; }
    public static string RawText { get; private set; } = "";
    public static List<string> Errors { get; private set; } = new();
    public static string? FilePath { get; private set; }
    public static bool Dirty { get; private set; }
    /// <summary>false = the file came from outside ASD: ask once before running it.</summary>
    public static bool Trusted { get; private set; } = true;
    private static readonly List<string> _undo = new();
    public static bool CanUndo => _undo.Count > 0;

    /// <summary>The document changed (cells, arrows, text, file name ...): the page rebuilds itself.</summary>
    public static event Action? Changed;

    // ── the run ──
    public static bool Running { get; private set; }
    public static readonly Dictionary<string, CellState> States = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, int?> Exits = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>(from cell id in lower case, arrow index) -> fired or not.</summary>
    public static readonly Dictionary<(string, int), bool> Arrows = new();
    public static readonly StringBuilder LogText = new();
    /// <summary>A cell or arrow changed state, or the run started / ended.</summary>
    public static event Action? RunChanged;
    public static event Action<string>? LogAdded;
    public static event Action<NoteKind, string>? RunFinished;
    /// <summary>The page that is showing now (the master-password dialog is shown on it).</summary>
    public static FrameworkElement? OwnerElement;

    private static CancellationTokenSource? _cts;
    private static AutomationRunner? _runner;
    private static readonly ConcurrentQueue<string> _logQueue = new();
    private static int _flushPending;
    private static readonly SemaphoreSlim KeyGate = new(1, 1);

    // ───────── start-up and the draft ─────────

    public static void Init(DispatcherQueue ui)
    {
        if (_inited) return;
        _inited = true;
        _ui = ui;
        LoadDraft();
    }

    private static string DraftFile => AppPaths.File("automation-draft.automation");

    private static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string StripPositions(string s)
        => string.Join("\n", Norm(s).Split('\n').Where(l => !l.TrimStart().StartsWith("at:", StringComparison.Ordinal)));

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Norm(text))))[..24];

    private static bool IsTrustedText(string text)
        => SettingsStore.Get("automation.trusted", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(Hash(text));

    private static void RememberTrusted(string text)
    {
        var list = SettingsStore.Get("automation.trusted", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var h = Hash(text);
        if (!list.Contains(h)) list.Add(h);
        if (list.Count > 200) list.RemoveRange(0, list.Count - 200);
        SettingsStore.Set("automation.trusted", string.Join(",", list));
    }

    /// <summary>Parses; a file with no cells at all is fine here (it is just an empty canvas).</summary>
    public static AutoFile ParseLoose(string text)
    {
        var f = AutomationParser.Parse(text);
        if (f.Cells.Count == 0 && f.Errors.Count == 1 && f.Errors[0].StartsWith("the file has no cells", StringComparison.Ordinal))
            f.Errors.Clear();
        return f;
    }

    public static string CurrentText() => Invalid ? RawText : AutomationWriter.ToText(Doc);

    private static void SaveDraft()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            System.IO.File.WriteAllText(DraftFile, CurrentText(), new UTF8Encoding(false));
            SettingsStore.Set("automation.path", FilePath ?? "");
        }
        catch { /* the draft is a convenience */ }
    }

    private static void LoadDraft()
    {
        try
        {
            if (!System.IO.File.Exists(DraftFile)) return;
            var text = System.IO.File.ReadAllText(DraftFile);
            var p = ParseLoose(text);
            if (p.Ok) { Doc = p; AutoLayout(Doc); }
            else { Invalid = true; RawText = text; Errors = p.Errors; }

            var path = SettingsStore.Get("automation.path", "");
            if (path.Length > 0 && System.IO.File.Exists(path))
            {
                FilePath = path;
                try
                {
                    var disk = ParseLoose(System.IO.File.ReadAllText(path));
                    Dirty = Invalid || !disk.Ok || StripPositions(AutomationWriter.ToText(disk)) != StripPositions(AutomationWriter.ToText(Doc));
                }
                catch { Dirty = true; }
            }
            else Dirty = Invalid || Doc.Cells.Count > 0;

            Trusted = Doc.Cells.Count == 0 || (!Invalid && IsTrustedText(CurrentText()));
        }
        catch { /* start with an empty canvas */ }
    }

    // ───────── editing ─────────

    private static void PushUndo(string text)
    {
        _undo.Add(text);
        if (_undo.Count > 50) _undo.RemoveAt(0);
    }

    private static void ClearResults()
    {
        if (Running) return;
        States.Clear(); Exits.Clear(); Arrows.Clear();
    }

    /// <summary>
    /// Changes the document on a copy, writes it as text and reads it back (so the parser checks it: unknown targets,
    /// loops, bad ids ...). Only a copy that passes becomes the document. Not allowed while a run is going.
    /// </summary>
    public static bool TryEdit(Action<AutoFile> mutate, out string error)
    {
        error = "";
        if (Running) { error = "Stop the run first, then change the automation."; return false; }
        if (Invalid) { error = "The text has errors. Fix them with the Text button first."; return false; }

        var before = AutomationWriter.ToText(Doc);
        var clone = ParseLoose(before);
        if (!clone.Ok) { error = "Internal error: " + string.Join("; ", clone.Errors); return false; }

        try { mutate(clone); }
        catch (Exception ex) { error = ex.Message; return false; }

        var check = ParseLoose(AutomationWriter.ToText(clone));
        if (!check.Ok) { error = string.Join("\n", check.Errors); return false; }

        AutoLayout(check);
        PushUndo(before);
        Doc = check;
        Dirty = true;
        ClearResults();
        SaveDraft();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Replaces everything with this text (the Text editor). Returns false and the errors if it does not parse.</summary>
    public static bool SetText(string text, out List<string> errors, bool trusted = false)
    {
        errors = new List<string>();
        if (Running) { errors.Add("Stop the run first."); return false; }
        var p = ParseLoose(text);
        if (!p.Ok) { errors = p.Errors; return false; }
        PushUndo(CurrentText());
        Doc = p;
        AutoLayout(Doc);
        Invalid = false; RawText = ""; Errors = new List<string>();
        Dirty = true;
        Trusted = trusted || p.Cells.Count == 0 || IsTrustedText(text);   // text typed or pasted in the editor is asked about once
        ClearResults();
        SaveDraft();
        Changed?.Invoke();
        return true;
    }

    public static void UndoLast()
    {
        if (Running || _undo.Count == 0) return;
        var text = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        var p = ParseLoose(text);
        if (!p.Ok) return;
        Doc = p;
        Invalid = false; RawText = "";
        Dirty = true;
        ClearResults();
        SaveDraft();
        Changed?.Invoke();
    }

    public static void NewDocument()
    {
        if (Running) return;
        Doc = new AutoFile();
        Invalid = false; RawText = ""; Errors = new List<string>();
        FilePath = null; Dirty = false; Trusted = true;
        _undo.Clear();
        ClearResults();
        LogText.Clear();
        SaveDraft();
        Changed?.Invoke();
    }

    /// <summary>Opens text read from a file. false = it has errors: the page then shows the text so it can be fixed.</summary>
    public static bool Open(string path, string text)
    {
        if (Running) return false;
        var p = ParseLoose(text);
        _undo.Clear();
        FilePath = path;
        ClearResults();
        if (p.Ok)
        {
            Doc = p;
            AutoLayout(Doc);
            Invalid = false; RawText = ""; Errors = new List<string>();
            Dirty = false;
            Trusted = p.Cells.Count == 0 || IsTrustedText(text);
        }
        else
        {
            Doc = new AutoFile();
            Invalid = true; RawText = text; Errors = p.Errors;
            Dirty = true; Trusted = false;
        }
        SaveDraft();
        Changed?.Invoke();
        return p.Ok;
    }

    /// <summary>Writes the file. Returns an error text, or null when it worked.</summary>
    public static string? Save(string path)
    {
        if (Invalid) return "The text has errors. Fix them with the Text button first.";
        try
        {
            var text = AutomationWriter.ToText(Doc);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
            RememberTrusted(text);
            FilePath = path;
            Dirty = false;
            Trusted = true;
            SaveDraft();
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The person confirmed that this text may run.</summary>
    public static void Trust()
    {
        Trusted = true;
        try { RememberTrusted(CurrentText()); } catch { }
    }

    /// <summary>Moving a card: kept in the document without an undo step.</summary>
    public static void MoveCell(string id, int x, int y)
    {
        var c = Doc.Find(id);
        if (c == null) return;
        c.X = x; c.Y = y;
        Dirty = true;
        SaveDraft();
    }

    /// <summary>Gives every card that has no position (X &lt; 0) a place: one column per "step away from the start", rows below each other.</summary>
    public static void AutoLayout(AutoFile f)
    {
        if (f.Cells.All(c => c.X >= 0 && c.Y >= 0)) return;

        var layer = f.Cells.ToDictionary(c => c.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        for (var round = 0; round < f.Cells.Count; round++)
        {
            var changed = false;
            foreach (var c in f.Cells)
                foreach (var a in c.Out)
                    if (layer.TryGetValue(a.To, out var l) && l < layer[c.Id] + 1) { layer[a.To] = layer[c.Id] + 1; changed = true; }
            if (!changed) break;
        }

        foreach (var c in f.Cells.Where(c => c.X < 0 || c.Y < 0).ToList())
        {
            var x = 40 + layer[c.Id] * 300;
            var y = 40;
            while (f.Cells.Any(o => o != c && o.X >= 0 && o.Y >= 0 && Math.Abs(o.X - x) < 260 && Math.Abs(o.Y - y) < 130)) y += 150;
            c.X = x; c.Y = y;
        }
    }

    // ───────── the run ─────────

    public static void Start(string? startAt, bool single)
    {
        if (Running || Invalid || Doc.Cells.Count == 0 || _ui == null) return;
        var runFile = ParseLoose(AutomationWriter.ToText(Doc));
        if (!runFile.Ok) return;

        Running = true;
        States.Clear(); Exits.Clear(); Arrows.Clear();
        LogText.Clear();
        _cts = new CancellationTokenSource();
        _runner = new AutomationRunner();
        var runner = _runner;
        var cts = _cts;
        var host = new Host();
        var ui = _ui;
        var name = FilePath != null ? System.IO.Path.GetFileNameWithoutExtension(FilePath) : (Doc.Name.Length > 0 ? Doc.Name : "Automation");
        var options = new AutomationOptions { StartAt = startAt, Single = single, MaxParallel = 4 };
        RunChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            AutomationResult? result = null;
            Exception? error = null;
            try { result = await runner.RunAsync(runFile, host, options, cts.Token); }
            catch (Exception ex) { error = ex; }
            ui.TryEnqueue(() => Finish(result, error, name, single));
        });
    }

    public static void Stop() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    public static void CancelCell(string id) => _runner?.CancelCell(id);

    public static void ClearLog() { if (!Running) LogText.Clear(); }

    private static void Finish(AutomationResult? r, Exception? ex, string name, bool single)
    {
        Running = false;
        Flush();

        NoteKind kind;
        string text;
        if (ex != null)
        {
            kind = NoteKind.Error;
            text = "Could not run: " + ex.Message;
            LogText.Append(text).Append("\r\n");
            LogAdded?.Invoke(text + "\r\n");
        }
        else if (r!.Cancelled) { kind = NoteKind.Warning; text = "Cancelled"; }
        else if (r.ExitCode == 0) { kind = NoteKind.Success; text = single ? "The cell finished OK" : "Finished OK"; }
        else { kind = NoteKind.Error; text = "Failed. Not handled by an arrow: " + string.Join(", ", r.Unhandled); }

        try { System.IO.File.WriteAllText(AppPaths.File("automation.log"), LogText.ToString()); } catch { }

        _cts?.Dispose();
        _cts = null;
        _runner = null;

        RunChanged?.Invoke();
        RunFinished?.Invoke(kind, text);
        if (!single) Notifier.Post(kind, "Automation: " + name, text, "automation");
    }

    private static void QueueLog(string line)
    {
        _logQueue.Enqueue(line);
        if (Interlocked.Exchange(ref _flushPending, 1) == 0) _ui?.TryEnqueue(Flush);
    }

    private static void Flush()
    {
        Interlocked.Exchange(ref _flushPending, 0);
        var sb = new StringBuilder();
        while (_logQueue.TryDequeue(out var line)) sb.Append(line).Append("\r\n");
        if (sb.Length == 0) return;
        var chunk = sb.ToString();
        LogText.Append(chunk);
        if (LogText.Length > 400_000) LogText.Remove(0, 100_000);
        LogAdded?.Invoke(chunk);
    }

    private static async Task<string> ExpandKeysOnUiAsync(string text, Host host)
    {
        var names = KeyRefs.Names(text);
        if (names.Count == 0) return text;

        var owner = OwnerElement ?? App.MainWindow?.Content as FrameworkElement;
        if (owner?.XamlRoot == null) throw new InvalidOperationException("the window is not available to ask for the master password");
        if (!await KeyUnlock.EnsureAsync(owner, "An automation uses a key from the Keys Store"))
            throw new InvalidOperationException("the Keys Store is locked (or the password dialog was cancelled)");

        var set = KeyRefs.Resolve(names);
        if (set.Missing.Count > 0)
            throw new InvalidOperationException("there is no key named " + string.Join(", ", set.Missing) + " in the Keys Store");
        foreach (var e in set.Expired) host.Log($"[{DateTime.Now:HH:mm:ss}] warning: the key \"{e}\" has expired");
        host.AddMask(KeyRefs.Masker(set));
        return KeyRefs.Replace(text, set);
    }

    /// <summary>What the engine uses to report to the window. Called from any thread; everything is moved to the UI thread.</summary>
    private sealed class Host : IAutomationHost
    {
        private readonly object _gate = new();
        private Func<string, string>[] _masks = Array.Empty<Func<string, string>>();

        public void AddMask(Func<string, string> mask)
        {
            lock (_gate) _masks = _masks.Append(mask).ToArray();
        }

        public void Log(string line) => QueueLog(line);

        public void CellChanged(string id, CellState state, int? exitCode)
            => _ui?.TryEnqueue(() => { States[id] = state; Exits[id] = exitCode; RunChanged?.Invoke(); });

        public void ArrowChanged(string from, int index, bool fired)
            => _ui?.TryEnqueue(() => { Arrows[(from.ToLowerInvariant(), index)] = fired; RunChanged?.Invoke(); });

        public void Notify(NoteKind kind, string title, string text)
            => Notifier.Post(kind, title, text, "automation", always: true);

        public async Task<string> ExpandKeysAsync(string text, CancellationToken ct)
        {
            await KeyGate.WaitAsync(ct);
            try
            {
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var queued = _ui != null && _ui.TryEnqueue(async () =>
                {
                    try { tcs.TrySetResult(await ExpandKeysOnUiAsync(text, this)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
                if (!queued) throw new InvalidOperationException("the window was closed");
                return await tcs.Task.WaitAsync(ct);
            }
            finally { KeyGate.Release(); }
        }

        public string Mask(string text)
        {
            Func<string, string>[] masks;
            lock (_gate) masks = _masks;
            foreach (var m in masks) text = m(text);
            return text;
        }
    }
}
