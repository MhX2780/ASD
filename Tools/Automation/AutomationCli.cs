using System.Runtime.InteropServices;
using System.Text;

namespace ASD;

/// <summary>
/// Headless use of the Automation engine (no window):
///   ASD.exe --check file.automation                    only read and validate the file
///   ASD.exe --roundtrip file.automation                write the file back as text, read it again and compare (self-test of the editor's saving)
///   ASD.exe --run   file.automation [--from cell | --only cell] [--parallel n] [--log out.txt]
/// Exit code: 0 ok · 1 a cell failed and no arrow handled it · 2 the file is invalid or the switches are wrong · 130 cancelled.
/// The log goes to --log (default: automation.log in the ASD data folder) and, if there is one, to the console.
/// </summary>
internal static class AutomationCli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);

    public static bool Wants(string[] args)
        => args.Skip(1).Any(a => a.Equals("--run", StringComparison.OrdinalIgnoreCase) || a.Equals("--check", StringComparison.OrdinalIgnoreCase)
                              || a.Equals("--roundtrip", StringComparison.OrdinalIgnoreCase));

    private sealed class HeadlessHost : IAutomationHost
    {
        private readonly object _gate = new();
        private readonly StreamWriter? _file;
        private TextWriter? _console;

        public HeadlessHost(string? logPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(logPath))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _file = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                }
            }
            catch { _file = null; }

            try
            {
                AttachConsole(0xFFFFFFFF);   // write into the console that started us, if any
                _console = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { _console = null; }
        }

        public void Log(string line)
        {
            lock (_gate)
            {
                try { _file?.WriteLine(line); } catch { /* disk problem: keep running */ }
                if (_console != null)
                {
                    try { _console.WriteLine(line); } catch { _console = null; }
                }
            }
        }

        public void Close() { lock (_gate) { try { _file?.Dispose(); } catch { } } }

        public void CellChanged(string id, CellState state, int? exitCode) { }
        public void ArrowChanged(string from, int index, bool fired) { }
        public void Notify(NoteKind kind, string title, string text) { }   // already written to the log by the engine

        public Task<string> ExpandKeysAsync(string text, CancellationToken ct)
            => throw new InvalidOperationException("{{key:...}} needs the Keys Store master password, so it only works when you run the automation from the ASD window");

        public string Mask(string text) => text;
    }

    private static string Signature(AutoFile f)
    {
        var sb = new StringBuilder();
        sb.Append("name=").Append(f.Name).Append('\n');
        foreach (var c in f.Cells)
        {
            sb.Append(c.Id).Append('|').Append(c.Title).Append('|').Append(c.Note).Append('|').Append(c.X).Append(',').Append(c.Y)
              .Append('|').Append(c.Step).Append('|').Append(c.Command).Append('|').Append(c.HttpMethod).Append('|').Append(c.HttpUrl)
              .Append('|').Append(c.Body).Append('|').Append(string.Join(";", c.Headers.Select(h => h.Key + "=" + h.Value)))
              .Append('|').Append(c.WaitSeconds).Append('|').Append(c.Level).Append('|').Append(c.TimeoutSeconds).Append('|').Append(c.Join).Append('|').Append(c.UploadFile).Append('|').Append(c.UploadTo).Append('\n');
            foreach (var a in c.Out) sb.Append("   ").Append(a.Kind).Append(a.Otherwise ? "|otherwise" : "").Append('|').Append(a.When).Append('|').Append(a.ExitCode).Append('|').Append(a.Contains).Append('|').Append(AutoArrow.NormalizeColor(a.Color)).Append('|').Append(a.Name).Append("->").Append(a.To).Append('\n');
        }
        return sb.ToString();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? file = null, startAt = null, logPath = null;
        var check = false;
        var roundtrip = false;
        var single = false;
        var parallel = 4;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) file = args[++i];
            else if (a.Equals("--check", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { file = args[++i]; check = true; }
            else if (a.Equals("--roundtrip", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { file = args[++i]; roundtrip = true; }
            else if (a.Equals("--from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) startAt = args[++i];
            else if (a.Equals("--only", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { startAt = args[++i]; single = true; }
            else if (a.Equals("--log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) logPath = args[++i];
            else if (a.Equals("--parallel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                int.TryParse(args[++i], out parallel);
        }

        var host = new HeadlessHost(logPath ?? AppPaths.File("automation.log"));
        try
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                host.Log("usage: ASD.exe --check file.automation | --run file.automation [--from cell] [--parallel n] [--log out.txt]");
                return 2;
            }

            var path = Path.GetFullPath(file.Trim().Trim('"'));
            if (!File.Exists(path))
            {
                host.Log($"error: file not found: {path}");
                return 2;
            }

            var parsed = AutomationParser.Parse(File.ReadAllText(path));
            foreach (var w in parsed.Warnings) host.Log("warning: " + w);
            if (!parsed.Ok)
            {
                foreach (var e in parsed.Errors) host.Log("error: " + e);
                host.Log($"INVALID  {parsed.Errors.Count} error(s) in {Path.GetFileName(path)}");
                return 2;
            }

            if (roundtrip)
            {
                var t1 = AutomationWriter.ToText(parsed);
                var again = AutomationParser.Parse(t1);
                if (!again.Ok)
                {
                    foreach (var e in again.Errors) host.Log("error: " + e);
                    host.Log("ROUNDTRIP FAILED  the written text does not parse");
                    return 1;
                }
                var t2 = AutomationWriter.ToText(again);
                if (Signature(parsed) != Signature(again) || t1 != t2)
                {
                    host.Log("ROUNDTRIP FAILED  the cells or arrows changed after writing and reading again");
                    host.Log("--- first ---"); host.Log(Signature(parsed));
                    host.Log("--- second ---"); host.Log(Signature(again));
                    return 1;
                }
                host.Log($"ROUNDTRIP OK  {parsed.Cells.Count} cells");
                return 0;
            }

            if (check)
            {
                host.Log($"VALID  {Path.GetFileName(path)}  ({parsed.Cells.Count} cells, {parsed.Cells.Sum(c => c.Out.Count)} arrows)");
                return 0;
            }

            var result = await new AutomationRunner().RunAsync(
                parsed, host, new AutomationOptions { StartAt = startAt, Single = single, MaxParallel = parallel }, CancellationToken.None);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            host.Log("error: " + ex.Message);
            return 2;
        }
        finally { host.Close(); }
    }
}
