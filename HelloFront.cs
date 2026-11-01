namespace ASD;

/// <summary>
/// The "Windows Security" prompt of Windows Hello can open BEHIND an unpackaged app window. While a Hello prompt is
/// pending, this finds that prompt (its window belongs to CredentialUIBroker.exe, whatever language Windows uses)
/// and brings only that window to the front. Nothing else is touched.
///   using var front = HelloFront.Watch();   // then await the Windows Hello call
/// </summary>
internal static class HelloFront
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

    private sealed class Stopper : IDisposable
    {
        public readonly CancellationTokenSource Cts = new();
        public void Dispose() { try { Cts.Cancel(); } catch { /* already stopped */ } }
    }

    public static IDisposable Watch()
    {
        var stopper = new Stopper();
        var token = stopper.Cts.Token;
        _ = Task.Run(async () =>
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            var raised = IntPtr.Zero;
            while (!token.IsCancellationRequested && DateTime.UtcNow < until)
            {
                try
                {
                    var prompt = FindPrompt();
                    if (prompt != IntPtr.Zero && prompt != raised)
                    {
                        SetWindowPos(prompt, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                        SetForegroundWindow(prompt);
                        raised = prompt;
                    }
                }
                catch { /* cosmetic: the prompt still works, it may just be behind */ }
                try { await Task.Delay(150, token); } catch { return; }
            }
        });
        return stopper;
    }

    private static IntPtr FindPrompt()
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.ProcessName.Equals("CredentialUIBroker", StringComparison.OrdinalIgnoreCase)) { found = hwnd; return false; }
            }
            catch { /* the process ended meanwhile */ }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
