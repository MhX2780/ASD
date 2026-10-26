using System.Globalization;

namespace ASD;

public sealed class TrackSample
{
    public double T, Cpu;
    public long Ws, Priv;
    public int Threads, Handles, Procs;
}

/// <summary>
/// Follows one program from launch (or attach) until it exits: CPU, memory, threads, handles,
/// child processes and (optionally) console output. The session lives here, not in the page,
/// so it keeps running while you use other tools.
/// </summary>
public static class ProcessTracker
{
    private sealed class Known
    {
        public Process? Proc;
        public string Name = "";
        public TimeSpan LastCpu;
        public bool Ended;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Known> KnownProcs = new();
    private static readonly List<string> Log = new();
    private static readonly List<TrackSample> Samples = new();
    private static CancellationTokenSource? _cts;
    private static Process? _root;
    private static DateTime _lastTick;
    private static double _totalCpuSec, _peakCpu;
    private static long _peakWs;
    private static int _peakThreads, _peakHandles, _peakProcs, _childrenStarted;
    private static bool _warned;

    private static bool _hasSession, _running;
    private static string _name = "";
    private static int _pid;
    private static DateTime _start;
    private static DateTime? _end;
    private static int? _exitCode;

    public static event Action? Changed;

    public static bool Running { get { lock (Gate) return _running; } }

    // ───────── starting / stopping ─────────
    public static void Launch(string path, string args, bool captureOutput)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) throw new InvalidOperationException("Choose a program to launch first.");
        if (!File.Exists(path)) throw new FileNotFoundException("Program not found: " + path);

        Stop();
        Reset();

        var psi = new ProcessStartInfo(path)
        {
            Arguments = args ?? "",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        if (captureOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
        }

        var p = new Process { StartInfo = psi };
        if (captureOutput)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) AddLog("[out] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) AddLog("[err] " + e.Data); };
        }

        var t0 = DateTime.Now;
        p.Start();
        if (captureOutput) { p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
        Begin(p, ("Launched " + Path.GetFileName(path) + " " + (args ?? "")).TrimEnd(), t0);
    }

    public static void Attach(int pid)
    {
        var p = Process.GetProcessById(pid);   // throws if it is already gone
        Stop();
        Reset();
        Begin(p, $"Attached to {SafeName(p)}", DateTime.Now);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            _end = DateTime.Now;
            AddLogLocked("■ Tracking stopped by you (the process itself is still running)");
        }
        _cts?.Cancel();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Stop();
        Reset();
        Changed?.Invoke();
    }

    private static void Reset()
    {
        lock (Gate)
        {
            foreach (var k in KnownProcs.Values) { try { k.Proc?.Dispose(); } catch { } }
            KnownProcs.Clear();
            Log.Clear();
            Samples.Clear();
            _root = null;
            _hasSession = _running = _warned = false;
            _end = null;
            _exitCode = null;
            _totalCpuSec = _peakCpu = 0;
            _peakWs = 0;
            _peakThreads = _peakHandles = _peakProcs = _childrenStarted = 0;
        }
    }

    private static void Begin(Process p, string description, DateTime startedAt)
    {
        lock (Gate)
        {
            _root = p;
            _pid = p.Id;
            _name = SafeName(p);
            _start = startedAt;
            try { _start = p.StartTime; } catch { }
            _lastTick = DateTime.Now;
            _hasSession = _running = true;
            KnownProcs[_pid] = new Known { Proc = p, Name = _name };
            try { _ = p.SafeHandle; } catch { }   // keeps the exit code readable after exit
            AddLogLocked($"▶ {description}  (PID {_pid})");
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token));
        Changed?.Invoke();
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                bool more;
                lock (Gate) more = Tick();
                Changed?.Invoke();
                if (!more) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    // ───────── one sample ─────────
    private static bool Tick()
    {
        if (!_running || _root == null) return false;
        var now = DateTime.Now;
        double dt = Math.Max(0.05, (now - _lastTick).TotalSeconds);
        _lastTick = now;

        // who is alive, and which of them belong to our process tree?
        var snap = ProcessSnapshot();
        var info = new Dictionary<int, (int ppid, string exe)>();
        foreach (var e in snap) info[e.pid] = (e.ppid, e.exe);

        var tree = new HashSet<int> { _pid };
        bool grew;
        do
        {
            grew = false;
            foreach (var e in snap)
                if (e.pid != e.ppid && tree.Contains(e.ppid) && tree.Add(e.pid)) grew = true;
        } while (grew);

        bool rootExited;
        try { rootExited = _root.HasExited; } catch { rootExited = !info.ContainsKey(_pid); }

        var alive = tree.Where(pid => info.ContainsKey(pid) && !(pid == _pid && rootExited)).ToList();

        // newly appeared children
        foreach (var pid in alive)
        {
            if (KnownProcs.ContainsKey(pid)) continue;
            Process? pr = null;
            try { pr = Process.GetProcessById(pid); _ = pr.SafeHandle; } catch { }
            var (ppid, exe) = info[pid];
            KnownProcs[pid] = new Known { Proc = pr, Name = exe };
            _childrenStarted++;
            AddLogLocked($"  ↳ child started: {exe}  (PID {pid}, parent {ppid})");
        }

        // measure
        double cpuDelta = 0;
        long ws = 0, priv = 0;
        int threads = 0, handles = 0;
        foreach (var (pid, k) in KnownProcs.ToList())
        {
            bool isAlive = alive.Contains(pid);
            if (!isAlive)
            {
                if (!k.Ended && pid != _pid)
                {
                    k.Ended = true;
                    string code = "";
                    try { if (k.Proc != null && k.Proc.HasExited) code = $", exit code {k.Proc.ExitCode}"; } catch { }
                    AddLogLocked($"  ↲ child exited: {k.Name}  (PID {pid}{code})");
                }
                continue;
            }
            if (k.Proc == null) continue;
            try
            {
                k.Proc.Refresh();
                var cpu = k.Proc.TotalProcessorTime;
                cpuDelta += Math.Max(0, (cpu - k.LastCpu).TotalSeconds);
                k.LastCpu = cpu;
                ws += k.Proc.WorkingSet64;
                priv += k.Proc.PrivateMemorySize64;
                threads += k.Proc.Threads.Count;
                handles += k.Proc.HandleCount;
            }
            catch (Exception ex)
            {
                if (pid == _pid && !_warned)
                {
                    _warned = true;
                    AddLogLocked($"  ⚠ Cannot read this process's metrics: {ex.Message} (try running ASD as administrator)");
                }
            }
        }

        double cpuPct = cpuDelta / (dt * Environment.ProcessorCount) * 100.0;
        _totalCpuSec += cpuDelta;
        var sample = new TrackSample
        {
            T = (now - _start).TotalSeconds, Cpu = cpuPct, Ws = ws, Priv = priv,
            Threads = threads, Handles = handles, Procs = alive.Count,
        };
        Samples.Add(sample);
        if (Samples.Count > 20000) Samples.RemoveRange(0, 2000);
        _peakCpu = Math.Max(_peakCpu, cpuPct);
        _peakWs = Math.Max(_peakWs, ws);
        _peakThreads = Math.Max(_peakThreads, threads);
        _peakHandles = Math.Max(_peakHandles, handles);
        _peakProcs = Math.Max(_peakProcs, alive.Count);

        if (!rootExited) return true;

        // the tracked program ended
        DateTime end = now;
        int? exit = null;
        try { end = _root.ExitTime; exit = _root.ExitCode; } catch { }
        _end = end;
        _exitCode = exit;
        _running = false;
        AddLogLocked($"■ Process exited{(exit != null ? $" with code {exit} (0x{exit:X})" : "")} after {Fmt(end - _start)}");
        var left = alive.Where(p => p != _pid).ToList();
        if (left.Count > 0)
            AddLogLocked($"  ⚠ {left.Count} child process(es) are still running: " + string.Join(", ", left.Select(p => $"{info[p].exe} ({p})")));
        return false;
    }

    // ───────── log ─────────
    public static void AddLog(string text)
    {
        lock (Gate) AddLogLocked(text);
        Changed?.Invoke();
    }

    private static void AddLogLocked(string text)
    {
        Log.Add($"[{Fmt(DateTime.Now - _start)}] {text}");
        if (Log.Count > 5000) Log.RemoveRange(0, 500);
    }

    // ───────── views for the page ─────────
    public static (string text, int count) LogText()
    {
        lock (Gate) return (string.Join(Environment.NewLine, Enumerable.Reverse(Log)), Log.Count);
    }

    public static string LiveText()
    {
        lock (Gate)
        {
            if (!_hasSession) return "No session yet.\nLaunch a program below, or attach to one that is already running.";
            var sb = new StringBuilder();
            var elapsed = (_running ? DateTime.Now : _end ?? DateTime.Now) - _start;
            string state = _running ? "● Running" : _exitCode != null ? $"■ Exited (code {_exitCode})" : "■ Finished";
            var c = Samples.LastOrDefault();

            sb.AppendLine($"Process    {_name}   (PID {_pid})");
            sb.AppendLine($"State      {state}");
            sb.AppendLine($"Started    {_start:yyyy-MM-dd HH:mm:ss}      Elapsed  {Fmt(elapsed)}");
            sb.AppendLine($"CPU        {(c?.Cpu ?? 0),6:0.0} %   {Spark(Samples.Select(s => s.Cpu), 100)}");
            sb.AppendLine($"Memory     {(c?.Ws ?? 0) / 1048576.0,6:0.0} MB (private {(c?.Priv ?? 0) / 1048576.0:0.0} MB)   {Spark(Samples.Select(s => (double)s.Ws), 0)}");
            sb.AppendLine($"Threads    {c?.Threads ?? 0}      Handles  {c?.Handles ?? 0}      Processes  {c?.Procs ?? 0}");
            sb.AppendLine();
            double avg = elapsed.TotalSeconds > 0.5 ? _totalCpuSec / (elapsed.TotalSeconds * Environment.ProcessorCount) * 100 : 0;
            sb.AppendLine($"Peak       CPU {_peakCpu:0.0} %   Memory {_peakWs / 1048576.0:0.0} MB   Threads {_peakThreads}   Handles {_peakHandles}   Processes {_peakProcs}");
            sb.Append($"Totals     Avg CPU {avg:0.0} %   CPU time {_totalCpuSec:0.0} s   Children started {_childrenStarted}");
            return sb.ToString();
        }
    }

    public static string ReportText()
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ASD — Process Tracker report");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine(LiveTextUnlocked());
            sb.AppendLine();
            sb.AppendLine("Timeline");
            sb.AppendLine(new string('-', 60));
            foreach (var l in Log) sb.AppendLine(l);
            sb.AppendLine();
            sb.AppendLine("Samples (CSV)");
            sb.AppendLine(new string('-', 60));
            sb.Append(CsvUnlocked());
            return sb.ToString();
        }
    }

    public static string CsvText() { lock (Gate) return CsvUnlocked(); }

    private static string LiveTextUnlocked() => LiveText();   // Monitor is re-entrant

    private static string CsvUnlocked()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("seconds,cpu_percent,working_set_mb,private_mb,threads,handles,processes\n");
        foreach (var s in Samples)
            sb.Append(s.T.ToString("0.0", inv)).Append(',').Append(s.Cpu.ToString("0.0", inv)).Append(',')
              .Append((s.Ws / 1048576.0).ToString("0.0", inv)).Append(',').Append((s.Priv / 1048576.0).ToString("0.0", inv)).Append(',')
              .Append(s.Threads).Append(',').Append(s.Handles).Append(',').Append(s.Procs).Append('\n');
        return sb.ToString();
    }

    // ───────── helpers ─────────
    private static string Fmt(TimeSpan t)
        => t < TimeSpan.Zero ? "00:00:00.0" : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

    private static string Spark(IEnumerable<double> values, double max)
    {
        const string bars = "▁▂▃▄▅▆▇█";
        var arr = values.TakeLast(48).ToArray();
        if (arr.Length == 0) return "";
        double top = max > 0 ? max : Math.Max(1, arr.Max());
        return new string(arr.Select(v => bars[Math.Clamp((int)Math.Round(v / top * 7), 0, 7)]).ToArray());
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName + ".exe"; } catch { return "process"; }
    }

    // ───────── process list (Toolhelp, no WMI needed) ─────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static List<(int pid, int ppid, string exe)> ProcessSnapshot()
    {
        var list = new List<(int pid, int ppid, string exe)>();
        var h = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(), szExeFile = "" };
            if (Process32FirstW(h, ref e))
            {
                do { list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile)); }
                while (Process32NextW(h, ref e));
            }
        }
        finally { CloseHandle(h); }
        return list;
    }
}
