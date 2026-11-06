using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ASD;

internal enum CellState { Pending, Ready, Running, Succeeded, Failed, Skipped, Cancelled }
internal enum EdgeState { Unknown, Fired, Dead }

/// <summary>What the engine needs from whoever runs it (the editor window, or the headless --run).</summary>
internal interface IAutomationHost
{
    /// <summary>One line of the run log (already time-stamped). May be called from any thread.</summary>
    void Log(string line);
    /// <summary>A cell changed state. May be called from any thread.</summary>
    void CellChanged(string id, CellState state, int? exitCode);
    /// <summary>An arrow was decided: fired (true) or not (false). It is the index-th arrow of cell "from" (same order as AutoCell.Out).</summary>
    void ArrowChanged(string from, int index, bool fired);
    void Notify(NoteKind kind, string title, string text);
    /// <summary>Replace {{key:Name}} placeholders. Throws InvalidOperationException if that is not possible.</summary>
    Task<string> ExpandKeysAsync(string text, CancellationToken ct);
    /// <summary>Hide secret values in text that is about to be logged.</summary>
    string Mask(string text);
}

internal sealed class AutomationOptions
{
    /// <summary>Run only this cell and everything after it ("Run from here"). Null = run everything.</summary>
    public string? StartAt;
    /// <summary>With StartAt: run only that one cell, no arrow is followed ("Run this cell").</summary>
    public bool Single;
    public int MaxParallel = 4;
}

internal sealed class AutomationResult
{
    public int ExitCode;
    public bool Cancelled;
    public readonly Dictionary<string, CellState> States = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Unhandled = new();
}

/// <summary>
/// Runs an <see cref="AutoFile"/>: cells are started as soon as their arrows allow, in parallel.
/// All decisions are made on one loop (no locks needed); only the steps themselves run on other threads.
/// </summary>
internal sealed class AutomationRunner
{
    private const int MaxStoredOutput = 64 * 1024;
    private const int MaxEnvOutput = 4000;
    private const int WhenTimeoutSeconds = 60;
    private const int HttpDefaultTimeoutSeconds = 30;
    private const int RunDefaultTimeoutSeconds = 3600;

    private static readonly Regex VarRx = new(@"\{\{\s*([A-Za-z0-9_-]+)\.(exitcode|output|status)\s*\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class Node
    {
        public readonly AutoCell Cell;
        public CellState State = CellState.Pending;
        public int Exit;
        public string Output = "";
        public int? Status;
        public int Seq;                 // the order cells finished in (a status cell reads the one that finished last)
        public bool Forced;
        public bool Handled;
        public readonly List<Edge> In = new();
        public readonly List<Edge> Out = new();
        public Node(AutoCell c) { Cell = c; }
    }

    private sealed class Edge
    {
        public readonly Node From, To;
        public readonly AutoArrow Arrow;
        public readonly int Index;
        public EdgeState State = EdgeState.Unknown;
        public Edge(Node from, Node to, AutoArrow a, int index) { From = from; To = to; Arrow = a; Index = index; }
    }

    private sealed record Snap(int Exit, string Output, int? Status);

    private sealed class Outcome
    {
        public int Exit;
        public string Output = "";
        public int? Status;
        public bool Cancelled;
        public bool[] Fire = Array.Empty<bool>();
    }

    private IAutomationHost _host = null!;
    private bool _single;
    private readonly object _cellGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _cellCts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stops one running cell (its process is killed). The rest of the run goes on; arrows from that cell do not fire.</summary>
    public void CancelCell(string id)
    {
        CancellationTokenSource? c;
        lock (_cellGate) _cellCts.TryGetValue(id, out c);
        try { c?.Cancel(); } catch (ObjectDisposedException) { /* it just finished */ }
    }

    private void Log(string msg) => _host.Log($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public async Task<AutomationResult> RunAsync(AutoFile file, IAutomationHost host, AutomationOptions? options, CancellationToken ct)
    {
        _host = host;
        options ??= new AutomationOptions();
        _single = options.Single && !string.IsNullOrWhiteSpace(options.StartAt);
        var result = new AutomationResult();

        var nodes = new List<Node>();
        var byId = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in file.Cells) { var n = new Node(c); nodes.Add(n); byId[c.Id] = n; }
        foreach (var n in nodes)
            for (var i = 0; i < n.Cell.Out.Count; i++)
            {
                var a = n.Cell.Out[i];
                var e = new Edge(n, byId[a.To], a, i);
                n.Out.Add(e);
                e.To.In.Add(e);
            }

        // "Run from here": only the start cell and what follows it take part.
        var outOfScope = new HashSet<Node>();
        if (!string.IsNullOrWhiteSpace(options.StartAt))
        {
            if (!byId.TryGetValue(options.StartAt.Trim(), out var start))
                throw new InvalidOperationException($"there is no cell named \"{options.StartAt}\"");
            var reach = new HashSet<Node>();
            var stack = new Stack<Node>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!reach.Add(n)) continue;
                if (_single) continue;             // "Run this cell": nothing after it takes part
                foreach (var e in n.Out) stack.Push(e.To);
            }
            foreach (var n in nodes)
                if (!reach.Contains(n)) { outOfScope.Add(n); n.State = CellState.Skipped; }
            foreach (var n in nodes)
                foreach (var e in n.Out)
                    if (outOfScope.Contains(n)) e.State = EdgeState.Dead;
            start.Forced = true;
            foreach (var e in start.In) e.State = EdgeState.Dead;
        }

        var running = new List<(Task<Outcome> Task, Node Node)>();
        var max = Math.Clamp(options.MaxParallel, 1, 16);

        Log($"START  {(file.Name.Length > 0 ? file.Name : "automation")}  ({nodes.Count} cells)");

        while (true)
        {
            Propagate(nodes, outOfScope);

            while (!ct.IsCancellationRequested && running.Count < max)
            {
                var next = nodes.FirstOrDefault(x => x.State == CellState.Ready);
                if (next == null) break;
                next.State = CellState.Running;
                host.CellChanged(next.Cell.Id, CellState.Running, null);
                Log($"RUN    {next.Cell.Id}  ({next.Cell.Display})");
                var snapshot = Snapshot(nodes);
                var env = BuildEnv(next, snapshot);
                var node = next;
                var cellCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_cellGate) _cellCts[node.Cell.Id] = cellCts;
                running.Add((Task.Run(async () =>
                {
                    try { return await RunCellAsync(node, snapshot, env, cellCts.Token); }
                    finally
                    {
                        lock (_cellGate) _cellCts.Remove(node.Cell.Id);
                        cellCts.Dispose();
                    }
                }), node));
            }

            if (running.Count == 0) break;

            var finished = await Task.WhenAny(running.Select(r => r.Task));
            var idx = running.FindIndex(r => r.Task == finished);
            var (_, done) = running[idx];
            running.RemoveAt(idx);
            Apply(done, await finished);
        }

        // Anything still waiting never got to run.
        foreach (var n in nodes)
            if (n.State is CellState.Pending or CellState.Ready)
            {
                n.State = ct.IsCancellationRequested ? CellState.Cancelled : CellState.Skipped;
                host.CellChanged(n.Cell.Id, n.State, null);
                if (n.State == CellState.Skipped) Log($"SKIP   {n.Cell.Id}");
            }

        result.Cancelled = ct.IsCancellationRequested || nodes.Any(x => x.State == CellState.Cancelled);
        foreach (var n in nodes)
        {
            result.States[n.Cell.Id] = n.State;
            if (n.State == CellState.Failed && !n.Handled) result.Unhandled.Add(n.Cell.Id);
        }

        Log("SUMMARY");
        foreach (var n in nodes)
        {
            if (outOfScope.Contains(n)) continue;
            var exit = n.State is CellState.Succeeded or CellState.Failed ? $"  exit {n.Exit}" : "";
            var handled = n.State == CellState.Failed && n.Handled ? "  (handled by an arrow)" : "";
            Log($"  {n.Cell.Id,-20} {n.State.ToString().ToUpperInvariant()}{exit}{handled}");
        }

        result.ExitCode = result.Cancelled ? 130 : result.Unhandled.Count > 0 ? 1 : 0;
        Log(result.ExitCode == 0 ? "RESULT OK"
            : result.Cancelled ? "RESULT CANCELLED"
            : "RESULT FAILED  (not handled: " + string.Join(", ", result.Unhandled) + ")");
        return result;
    }

    // ── scheduling ──

    private void Propagate(List<Node> nodes, HashSet<Node> outOfScope)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var n in nodes)
            {
                if (n.State != CellState.Pending) continue;
                if (n.Forced || n.In.Count == 0)
                {
                    n.State = CellState.Ready;
                    changed = true;
                    continue;
                }

                var fired = n.In.Count(e => e.State == EdgeState.Fired);
                var unknown = n.In.Count(e => e.State == EdgeState.Unknown);
                var go = false;
                var skip = false;
                switch (n.Cell.Join)
                {
                    case JoinMode.First:
                        if (fired > 0) go = true; else if (unknown == 0) skip = true;
                        break;
                    case JoinMode.All:
                        if (unknown == 0) { if (fired == n.In.Count) go = true; else skip = true; }
                        break;
                    default:
                        if (unknown == 0) { if (fired > 0) go = true; else skip = true; }
                        break;
                }

                if (go) { n.State = CellState.Ready; changed = true; }
                else if (skip)
                {
                    n.State = CellState.Skipped;
                    foreach (var e in n.Out)
                    {
                        e.State = EdgeState.Dead;
                        _host.ArrowChanged(n.Cell.Id, e.Index, false);
                    }
                    _host.CellChanged(n.Cell.Id, CellState.Skipped, null);
                    Log($"SKIP   {n.Cell.Id}  (no arrow into it fired)");
                    changed = true;
                }
            }
        } while (changed);
    }

    private int _seq;

    private void Apply(Node n, Outcome o)
    {
        n.Seq = ++_seq;
        n.Exit = o.Exit;
        n.Output = o.Output;
        n.Status = o.Status;

        if (o.Cancelled)
        {
            n.State = CellState.Cancelled;
            foreach (var e in n.Out) { e.State = EdgeState.Dead; _host.ArrowChanged(n.Cell.Id, e.Index, false); }
            _host.CellChanged(n.Cell.Id, CellState.Cancelled, null);
            Log($"CANCEL {n.Cell.Id}");
            return;
        }

        n.State = o.Exit == 0 ? CellState.Succeeded : CellState.Failed;
        _host.CellChanged(n.Cell.Id, n.State, o.Exit);
        Log($"{(o.Exit == 0 ? "DONE  " : "FAIL  ")} {n.Cell.Id}  exit {o.Exit}");

        for (var i = 0; i < n.Out.Count; i++)
        {
            var e = n.Out[i];
            var fire = i < o.Fire.Length && o.Fire[i];
            e.State = fire ? EdgeState.Fired : EdgeState.Dead;
            _host.ArrowChanged(n.Cell.Id, e.Index, fire);
            if (!_single) Log($"  -> {e.To.Cell.Id}  [{e.Arrow.Label}]  {(fire ? "fired" : "not fired")}");
            if (fire && n.State == CellState.Failed) n.Handled = true;
        }
    }

    private static Dictionary<string, Snap> Snapshot(List<Node> nodes)
    {
        var d = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes)
            if (n.State is CellState.Succeeded or CellState.Failed)
                d[n.Cell.Id] = new Snap(n.Exit, n.Output, n.Status);
        return d;
    }

    private static string EnvName(string id) => id.ToUpperInvariant().Replace('-', '_');

    /// <summary>Results of the cells that point at this one, as environment variables (safe to read from any command).</summary>
    private static Dictionary<string, string> BuildEnv(Node n, Dictionary<string, Snap> snap)
    {
        var env = new Dictionary<string, string> { ["ASD_CELL"] = n.Cell.Id };
        foreach (var e in n.In)
        {
            if (!snap.TryGetValue(e.From.Cell.Id, out var s)) continue;
            var name = EnvName(e.From.Cell.Id);
            env["ASD_" + name + "_EXITCODE"] = s.Exit.ToString();
            env["ASD_" + name + "_OUTPUT"] = Cap(s.Output, MaxEnvOutput);
            if (s.Status != null) env["ASD_" + name + "_STATUS"] = s.Status.Value.ToString();
        }
        return env;
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    // ── one cell ──

    private async Task<Outcome> RunCellAsync(Node n, Dictionary<string, Snap> snap, Dictionary<string, string> env, CancellationToken ct)
    {
        var c = n.Cell;
        var o = new Outcome();
        try
        {
            ct.ThrowIfCancellationRequested();
            switch (c.Step)
            {
                case StepKind.Run:
                {
                    var cmd = await ExpandAsync(c.Command, snap, allowOutput: false, ct);
                    Log($"  $ {_host.Mask(cmd)}");
                    var (exit, output) = await RunProcessAsync(cmd, env, c.TimeoutSeconds > 0 ? c.TimeoutSeconds : RunDefaultTimeoutSeconds, ct);
                    o.Exit = exit; o.Output = output;
                    break;
                }
                case StepKind.Http:
                    await RunHttpAsync(c, snap, o, ct);
                    break;
                case StepKind.Notify:
                {
                    var text = await ExpandAsync(c.Command, snap, allowOutput: true, ct);
                    var kind = c.Level switch { "success" => NoteKind.Success, "error" => NoteKind.Error, "warning" => NoteKind.Warning, _ => NoteKind.Info };
                    Log($"  notify [{c.Level}] {_host.Mask(text)}");
                    _host.Notify(kind, c.Display, text);
                    o.Output = text;
                    break;
                }
                case StepKind.Status:
                {
                    // takes over the result of the cell that sent it here, so its own arrows can decide by that result
                    var from = n.In.Where(e => e.State == EdgeState.Fired).OrderByDescending(e => e.From.Seq).Select(e => e.From).FirstOrDefault()
                               ?? n.In.OrderByDescending(e => e.From.Seq).Select(e => e.From).FirstOrDefault();
                    if (from == null) { Log("  status: nothing points here, so the result is exit 0"); break; }
                    o.Exit = from.Exit; o.Output = from.Output; o.Status = from.Status;
                    Log($"  status: {from.Cell.Id} ended with exit {from.Exit}{(from.Status != null ? $", status {from.Status}" : "")}");
                    break;
                }
                case StepKind.Upload:
                    await RunUploadAsync(c, snap, o, ct);
                    break;
                case StepKind.Wait:
                    Log($"  waiting {c.WaitSeconds:0.##} s");
                    await Task.Delay(TimeSpan.FromSeconds(c.WaitSeconds), ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            o.Cancelled = true;
            return o;
        }
        catch (Exception ex)
        {
            o.Exit = -1;
            o.Output = ex.Message;
            Log($"  error: {_host.Mask(ex.Message)}");
        }

        // Decide the arrows (a "when" command may run here). "Run this cell" follows no arrow.
        o.Fire = new bool[n.Out.Count];
        if (_single) return o;
        var anyFired = false;
        for (var i = 0; i < n.Out.Count; i++)
        {
            var a = n.Out[i].Arrow;
            if (a.Otherwise) continue;                      // decided below, when the others are known
            var match = a.Kind switch { ArrowKind.Always => true, ArrowKind.Success => o.Exit == 0, _ => o.Exit != 0 };
            if (match && a.ExitCode != null) match = o.Exit == a.ExitCode.Value;
            if (match && a.Contains != null) match = (o.Output ?? "").Contains(a.Contains, StringComparison.OrdinalIgnoreCase);
            if (match && a.When != null)
            {
                try { match = await EvalWhenAsync(a.When, o, env, snap, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { o.Cancelled = true; return o; }
                catch (Exception ex) { match = false; Log($"  when: error: {_host.Mask(ex.Message)}"); }
            }
            o.Fire[i] = match;
            anyFired |= match;
        }
        for (var i = 0; i < n.Out.Count; i++)
            if (n.Out[i].Arrow.Otherwise) o.Fire[i] = !anyFired;
        return o;
    }

    private async Task<bool> EvalWhenAsync(string command, Outcome o, Dictionary<string, string> env, Dictionary<string, Snap> snap, CancellationToken ct)
    {
        var cmd = await ExpandAsync(command, snap, allowOutput: false, ct);
        var env2 = new Dictionary<string, string>(env)
        {
            ["ASD_EXIT_CODE"] = o.Exit.ToString(),
            ["ASD_OUTPUT"] = Cap(o.Output, MaxEnvOutput),
        };
        var (exit, _) = await RunProcessAsync(cmd, env2, WhenTimeoutSeconds, ct);
        Log($"  when: {_host.Mask(cmd)}  ->  exit {exit}");
        return exit == 0;
    }

    /// <summary>
    /// {{cell.exitcode}} and {{cell.status}} are numbers and can go anywhere.
    /// {{cell.output}} is free text, so it is NOT allowed inside commands (a command could be injected):
    /// commands read it from the ASD_&lt;CELL&gt;_OUTPUT environment variable instead.
    /// </summary>
    private async Task<string> ExpandAsync(string text, Dictionary<string, Snap> snap, bool allowOutput, CancellationToken ct)
    {
        var s = VarRx.Replace(text, m =>
        {
            var id = m.Groups[1].Value;
            var what = m.Groups[2].Value.ToLowerInvariant();
            if (!snap.TryGetValue(id, out var v))
                throw new InvalidOperationException($"{{{{{id}.{what}}}}} cannot be used: cell \"{id}\" has not finished before this one");
            switch (what)
            {
                case "exitcode": return v.Exit.ToString();
                case "status": return v.Status?.ToString() ?? "";
                default:
                    if (!allowOutput)
                        throw new InvalidOperationException($"{{{{{id}.output}}}} is not allowed inside a command; read the environment variable ASD_{EnvName(id)}_OUTPUT instead");
                    return v.Output;
            }
        });
        if (Regex.IsMatch(s, @"\{\{\s*key:", RegexOptions.IgnoreCase))
            s = await _host.ExpandKeysAsync(s, ct);
        return s;
    }

    // ── steps ──

    private async Task RunHttpAsync(AutoCell c, Dictionary<string, Snap> snap, Outcome o, CancellationToken ct)
    {
        var url = await ExpandAsync(c.HttpUrl, snap, allowOutput: true, ct);
        var body = c.Body.Length > 0 ? await ExpandAsync(c.Body, snap, allowOutput: true, ct) : "";
        Log($"  {c.HttpMethod} {_host.Mask(url)}");

        using var req = new HttpRequestMessage(new HttpMethod(c.HttpMethod), url);
        if (body.Length > 0) req.Content = new StringContent(body, Encoding.UTF8);
        foreach (var h in c.Headers)
        {
            var val = await ExpandAsync(h.Value, snap, allowOutput: true, ct);
            if (h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && req.Content != null)
            {
                req.Content.Headers.Remove(h.Key);
                req.Content.Headers.TryAddWithoutValidation(h.Key, val);
            }
            else req.Headers.TryAddWithoutValidation(h.Key, val);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(c.TimeoutSeconds > 0 ? c.TimeoutSeconds : HttpDefaultTimeoutSeconds));
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            var code = (int)resp.StatusCode;
            o.Status = code;
            o.Exit = code is >= 200 and <= 299 ? 0 : code;
            o.Output = Cap(text, MaxStoredOutput);
            Log($"  HTTP {code} {resp.ReasonPhrase}  ({text.Length} chars)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            o.Exit = -1;
            o.Output = "timed out";
            Log("  error: the request timed out");
        }
    }

    private async Task RunUploadAsync(AutoCell c, Dictionary<string, Snap> snap, Outcome o, CancellationToken ct)
    {
        var file = (await ExpandAsync(c.UploadFile, snap, allowOutput: true, ct)).Trim();
        var to = await ExpandAsync(c.UploadTo, snap, allowOutput: true, ct);
        var target = UploadEngine.ParseTarget(to, out var where);
        if (target == null)
        {
            o.Exit = 1; o.Output = "unknown upload target";
            Log($"  error: \"{_host.Mask(to)}\" is not dropbox / post / put");
            return;
        }

        var opt = new UploadOptions { Target = target.Value, FilePath = file, Where = where, TimeoutSeconds = c.TimeoutSeconds, ShareLink = true };
        foreach (var h in c.Headers) opt.Headers.Add((h.Key, await ExpandAsync(h.Value, snap, allowOutput: true, ct)));

        string? appKey = null, refresh = null;
        if (target == UploadTarget.Dropbox)
        {
            appKey = DropboxStore.AppKey;
            refresh = await _host.ExpandKeysAsync("{{key:" + DropboxStore.RefName + "}}", ct);
        }

        Log($"  upload {_host.Mask(file)}  ->  {target.Value.ToString().ToLowerInvariant()} {_host.Mask(where)}");
        var r = await UploadEngine.RunAsync(opt, appKey, refresh, null, ct);
        o.Exit = r.Ok ? 0 : 1;
        o.Output = Cap(r.Output, MaxStoredOutput);
        o.Status = r.Status > 0 ? r.Status : null;
        Log($"  upload: {_host.Mask(r.Message)}");
    }

    private async Task<(int Exit, string Output)> RunProcessAsync(string command, Dictionary<string, string> env, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = "/d /s /c \"" + command + "\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var gate = new object();
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (gate) { if (sb.Length < MaxStoredOutput) sb.AppendLine(e.Data); }
            Log("  | " + _host.Mask(e.Data));
        }
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;

        p.Start();
        try { p.StandardInput.Close(); } catch { /* the command did not want input */ }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            try { await p.WaitForExitAsync(); } catch { /* ignore */ }
            if (ct.IsCancellationRequested) throw;
            Log($"  error: the command took longer than {timeoutSeconds} s and was stopped");
            return (-1, "timed out");
        }
        p.WaitForExit();   // let the last output lines arrive
        string text;
        lock (gate) text = sb.ToString().TrimEnd();
        return (p.ExitCode, text);
    }
}
