using System.IO.Compression;
using System.Net.Http.Headers;

namespace ASD;

/// <summary>Result of comparing the update.txt shipped with this build against the one on GitHub.</summary>
internal readonly record struct UpdateCheckResult(bool Available, string? LocalText, string RemoteText);

/// <param name="Stage">"download", "extract" or "install".</param>
internal readonly record struct UpdateProgress(
    string Stage, double Percent, bool Indeterminate, long Received, long Total, double BytesPerSecond);

/// <summary>
/// Self-update logic (no UI):
///   1. Compare the update.txt next to ASD.exe with the one in the latest GitHub release.
///   2. If they differ: download ASD.zip, extract it to a staging folder, then start a hidden .bat
///      and exit. The .bat waits for this process to end (files are locked while it runs), copies
///      the new files over the installation folder and starts ASD again.
/// </summary>
internal static class UpdateService
{
    public const string RemoteInfoUrl = "https://github.com/MhX2780/ASD/releases/download/latest/update.txt";
    public const string RemoteZipUrl  = "https://github.com/MhX2780/ASD/releases/download/latest/ASD.zip";

    /// <summary>Shown when the installation folder can't be written to (read-only folder, CD/DVD, no permission).</summary>
    public const string NotWritableMessage = "Update Failed - CD/R or directory is not writable";

    private const int MaxInfoBytes = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Timeouts are handled per request/read below: the zip download can legitimately take minutes.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ASD-Updater/1.0");
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return client;
    }

    // ───────── paths ─────────

    /// <summary>The folder ASD.exe runs from (no trailing slash: it is passed to a .bat file).</summary>
    public static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    public static string LocalInfoPath => Path.Combine(AppDir, "update.txt");

    private static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD", "update");

    private static string StagingDir => Path.Combine(WorkDir, "staging");
    private static string ZipPath => Path.Combine(WorkDir, "ASD.zip");

    // ───────── step 0: can we write to the installation folder? ─────────

    /// <summary>
    /// True when a file can really be created in the folder ASD runs from. Fails for read-only
    /// media (CD/DVD), write-protected folders and folders that need administrator rights.
    /// </summary>
    public static bool IsInstallFolderWritable()
    {
        try
        {
            var probe = Path.Combine(AppDir, $".asd-write-test-{Guid.NewGuid():N}.tmp");
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            fs.WriteByte(0);
            fs.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ───────── step 1: is there a newer version? ─────────

    /// <summary>The update.txt of this build, or null when there is none (e.g. a build run from Visual Studio).</summary>
    public static string? ReadLocal()
    {
        try { return File.Exists(LocalInfoPath) ? File.ReadAllText(LocalInfoPath) : null; }
        catch { return null; }
    }

    public static string Normalize(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿', ' ', '\t', '\n');

    /// <summary>Short text for dialogs: the update.txt contents, trimmed.</summary>
    public static string Describe(string? text)
    {
        var t = Normalize(text);
        return t.Length > 1200 ? t[..1200] + "…" : t;
    }

    /// <summary>Downloads the release's update.txt and compares it with the local one. Throws when GitHub can't be reached.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        string remote;
        try
        {
            using var response = await Http.GetAsync(RemoteInfoUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxInfoBytes)
                throw new InvalidDataException("The update information file is unexpectedly large.");
            remote = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub did not answer in time. Check your internet connection.");
        }

        // Guard against captive-portal / error pages: a real update.txt always has a "SHA:" line.
        if (remote.Length > MaxInfoBytes || !remote.Contains("SHA:", StringComparison.Ordinal))
            throw new InvalidDataException("The update information on GitHub is not valid.");

        var local = ReadLocal();
        var available = local == null || Normalize(local) != Normalize(remote);
        return new UpdateCheckResult(available, local, remote);
    }

    // ───────── step 2: download + extract ─────────

    public static async Task DownloadAndExtractAsync(IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        TryDeleteDirectory(StagingDir);
        TryDeleteFile(ZipPath);
        TryDeleteFile(ZipPath + ".part");

        await DownloadAsync(progress, ct);
        await Task.Run(() => Extract(progress, ct), ct);
    }

    private static async Task DownloadAsync(IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        var part = ZipPath + ".part";
        progress.Report(new UpdateProgress("download", 0, true, 0, 0, 0));

        try
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerTimeout.CancelAfter(ConnectTimeout);

            using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, RemoteZipUrl),
                HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var clock = Stopwatch.StartNew();
                long lastReportMs = -1000;

                // Every read gets its own "stall" timer: the download may be long, but it must keep moving.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    idle.CancelAfter(StallTimeout);
                    int read = await source.ReadAsync(buffer.AsMemory(), idle.Token);
                    if (read == 0) break;

                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;

                    if (clock.ElapsedMilliseconds - lastReportMs >= 100)
                    {
                        lastReportMs = clock.ElapsedMilliseconds;
                        var speed = received / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                        progress.Report(new UpdateProgress("download",
                            total > 0 ? received * 100.0 / total : 0, total <= 0, received, total, speed));
                    }
                }

                if (total > 0 && received != total)
                    throw new IOException($"The download was incomplete ({received:N0} of {total:N0} bytes).");

                progress.Report(new UpdateProgress("download", 100, false, received, Math.Max(total, received),
                    received / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
            }

            File.Move(part, ZipPath, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDeleteFile(part);
            throw new TimeoutException("The download stalled. Check your internet connection and try again.");
        }
        catch
        {
            TryDeleteFile(part);
            throw;
        }
    }

    private static void Extract(IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingDir);
        var root = Path.GetFullPath(StagingDir) + Path.DirectorySeparatorChar;

        using (var zip = ZipFile.OpenRead(ZipPath))
        {
            var entries = zip.Entries.ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];

                // Zip-slip protection: never write outside the staging folder.
                var dest = Path.GetFullPath(Path.Combine(StagingDir, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update package contains an invalid path.");

                if (entry.Name.Length == 0) { Directory.CreateDirectory(dest); continue; }   // folder entry
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);

                progress.Report(new UpdateProgress("extract", (i + 1) * 100.0 / entries.Count, false, 0, 0, 0));
            }
        }

        // The package must be complete: the app itself, and update.txt (otherwise the new version
        // would not know its own version and would offer the same update again and again).
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "ASD.exe");
        if (!File.Exists(Path.Combine(StagingDir, exeName)))
            throw new InvalidDataException($"The update package is invalid ({exeName} is missing).");
        if (!File.Exists(Path.Combine(StagingDir, "update.txt")))
            throw new InvalidDataException("The update package is invalid (update.txt is missing).");
    }

    // ───────── step 3: replace the files and restart ─────────

    /// <summary>
    /// Starts the hidden updater script. The caller must exit the app right after this returns:
    /// the script waits for this process to end before it replaces any file.
    /// </summary>
    public static void StartInstaller()
    {
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "ASD.exe");
        if (!Directory.Exists(StagingDir))
            throw new DirectoryNotFoundException("The downloaded update could not be found.");

        var batPath = Path.Combine(WorkDir, "apply-update.bat");
        File.WriteAllText(batPath, string.Join("\r\n", BatLines) + "\r\n", Encoding.ASCII);

        var psi = new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkDir,
        };
        // Paths go in as arguments (not written into the .bat) so non-English folder names work.
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(AppDir);
        psi.ArgumentList.Add(StagingDir);
        psi.ArgumentList.Add(exeName);
        psi.ArgumentList.Add(WorkDir);
        Process.Start(psi);
    }

    // Plain ASCII on purpose: cmd.exe reads .bat files in the OEM code page.
    private static readonly string[] BatLines =
    {
        "@echo off",
        "setlocal EnableExtensions",
        "set \"PID=%~1\"",
        "set \"APPDIR=%~2\"",
        "set \"STAGE=%~3\"",
        "set \"EXE=%~4\"",
        "set \"WORK=%~5\"",
        "set \"LOG=%WORK%\\update.log\"",
        "echo [%date% %time%] ASD updater started. Waiting for process %PID% to exit.> \"%LOG%\"",
        "",
        "rem --- 1. wait until ASD has fully exited (its files are locked while it runs), max 60 s ---",
        "set /a TRIES=0",
        ":wait",
        "tasklist /FI \"PID eq %PID%\" /FO CSV /NH 2>nul | find \"\"\"%PID%\"\"\" >nul",
        "if errorlevel 1 goto copy",
        "set /a TRIES+=1",
        "if %TRIES% GEQ 60 goto copy",
        "ping -n 2 127.0.0.1 >nul",
        "goto wait",
        "",
        "rem --- 2. copy the new files (everything except update.txt) over the installation ---",
        ":copy",
        "echo [%date% %time%] Copying files...>> \"%LOG%\"",
        "robocopy \"%STAGE%\" \"%APPDIR%\" /E /IS /IT /XF update.txt /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
        "set RC=%ERRORLEVEL%",
        "if %RC% GEQ 8 goto failed",
        "",
        "rem --- 3. update.txt goes last: it is only replaced when everything else was copied, so a",
        "rem        half-installed update is offered again on the next start ---",
        "robocopy \"%STAGE%\" \"%APPDIR%\" update.txt /IS /IT /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >> \"%LOG%\" 2>&1",
        "set RC=%ERRORLEVEL%",
        "if %RC% GEQ 8 goto failed",
        "echo [%date% %time%] Update installed.>> \"%LOG%\"",
        "goto restart",
        "",
        ":failed",
        "echo [%date% %time%] UPDATE FAILED (robocopy exit code %RC%). Starting the old version again.>> \"%LOG%\"",
        "",
        ":restart",
        "start \"\" /D \"%APPDIR%\" \"%APPDIR%\\%EXE%\"",
        "rmdir /s /q \"%STAGE%\" >nul 2>&1",
        "del /f /q \"%WORK%\\ASD.zip\" >nul 2>&1",
        "(goto) 2>nul & del /f /q \"%~f0\"",
    };

    // ───────── helpers ─────────

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
