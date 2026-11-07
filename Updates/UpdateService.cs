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

    // Installed (MSIX) ASD: msix.txt = "Version: 1.0.N.0", "SHA256: <hex>", then the same commit text as update.txt.
    public const string MsixInfoUrl = "https://github.com/MhX2780/ASD/releases/download/latest/msix.txt";
    public const string MsixUrl     = "https://github.com/MhX2780/ASD/releases/download/latest/ASD.msix";

    /// <summary>True when this ASD can look for updates (an installed ASD always can; a plain folder needs its update.txt).</summary>
    public static bool CanCheck => AppIdentity.IsPackaged || ReadLocal() != null;

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

    private static string WorkDir => AppPaths.File("update");

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
        if (AppIdentity.IsPackaged)
        {
            var msix = await CheckMsixAsync(ct);
            return new UpdateCheckResult(msix.Available, $"Version {msix.Installed}", msix.Text);
        }

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
        // (a .part file from an interrupted download is kept: the download continues from it)

        await DownloadAsync(RemoteZipUrl, ZipPath, progress, ct);
        await Task.Run(() => Extract(progress, ct), ct);
        await StageInfoAsync(ct);
    }

    // ───────── installed (MSIX) ASD ─────────

    /// <param name="Installed">The version that is installed now.</param>
    /// <param name="Remote">The version in the release.</param>
    /// <param name="Sha256">Hash of ASD.msix, from msix.txt.</param>
    /// <param name="Text">The whole msix.txt (shown to the person).</param>
    internal readonly record struct MsixCheck(bool Available, Version Installed, Version Remote, string Sha256, string Text);

    private static string MsixPath => Path.Combine(WorkDir, "ASD.msix");

    /// <summary>Reads msix.txt from the release and compares its version with the installed package. Throws when GitHub can't be reached.</summary>
    public static async Task<MsixCheck> CheckMsixAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        string text;
        try
        {
            using var response = await Http.GetAsync(MsixInfoUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            EnsureReleaseFile(response);
            if (response.Content.Headers.ContentLength > MaxInfoBytes)
                throw new InvalidDataException("The update information file is unexpectedly large.");
            text = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub did not answer in time. Check your internet connection.");
        }

        string? versionText = null, hash = null;
        var shown = new List<string>();   // what the person reads: msix.txt without the long SHA-256 line
        foreach (var raw in Normalize(text).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) { versionText = line["Version:".Length..].Trim(); shown.Add(raw); }
            else if (line.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) hash = line["SHA256:".Length..].Trim();
            else shown.Add(raw);
        }
        if (text.Length > MaxInfoBytes || !Version.TryParse(versionText, out var remote) || hash is not { Length: 64 })
            throw new InvalidDataException("The update information on GitHub is not valid.");

        var installed = AppIdentity.PackageVersion ?? new Version(0, 0, 0, 0);
        return new MsixCheck(remote > installed, installed, remote, hash.ToLowerInvariant(), string.Join("\n", shown));
    }

    /// <summary>Downloads ASD.msix and checks it against the hash from msix.txt.</summary>
    public static async Task DownloadMsixAsync(MsixCheck info, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        TryDeleteFile(MsixPath);
        // (a .part file from an interrupted download is kept: the download continues from it)

        await DownloadAsync(MsixUrl, MsixPath, progress, ct);

        progress.Report(new UpdateProgress("verify", 100, true, 0, 0, 0));
        string actual;
        await using (var fs = new FileStream(MsixPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();

        if (actual != info.Sha256)
        {
            TryDeleteFile(MsixPath);
            throw new InvalidDataException("The downloaded package is damaged or incomplete. Try again.");
        }
    }

    private const string UpdateTaskName = "ASD-Update";
    private static string FailedFile => Path.Combine(WorkDir, "update-failed.txt");
    private static string UpdateLog => Path.Combine(WorkDir, "update-log.txt");

    private static string Ps(string text) => "'" + text.Replace("'", "''") + "'";   // a PowerShell string literal

    /// <summary>
    /// Installs the downloaded package with no window and no question, then starts ASD again. ASD has to close for this, and
    /// anything ASD starts itself belongs to ASD's package and is closed with it, so the work is handed to Windows' Task Scheduler:
    /// a hidden PowerShell (outside the package) waits for ASD to exit, runs Add-AppxPackage, and opens the new ASD.
    /// If the install fails, the old ASD is opened again and the reason is kept for the next start (TakeFailedUpdateMessage).
    /// Returns false when the helper could not be set up: the caller then falls back to Windows' installer window.
    /// The caller exits ASD right after a true.
    /// </summary>
    public static bool StartSilentMsixInstall()
    {
        try
        {
            if (!File.Exists(MsixPath)) return false;
            Directory.CreateDirectory(WorkDir);
            TryDeleteFile(FailedFile);

            var script = Path.Combine(WorkDir, "apply-update.ps1");
            File.WriteAllText(script, $$"""
                $msix = {{Ps(MsixPath)}}
                $target = {{Ps(AppIdentity.LaunchTarget ?? "")}}
                $log = {{Ps(UpdateLog)}}
                $failFile = {{Ps(FailedFile)}}
                $waitPid = {{Environment.ProcessId}}
                function Log($m) { Add-Content -LiteralPath $log -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $m) }
                $failed = $null
                try {
                  Log "update started; waiting for ASD (pid $waitPid) to close"
                  $p = Get-Process -Id $waitPid -ErrorAction SilentlyContinue
                  if ($p) { $null = $p.WaitForExit(30000) }
                  Log "installing $msix"
                  Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion -ForceApplicationShutdown -ErrorAction Stop
                  Log 'installed'
                } catch {
                  $failed = $_.Exception.Message
                  Log "FAILED: $failed"
                }
                if ($failed) { Set-Content -LiteralPath $failFile -Value $failed }
                Start-Process explorer.exe $target
                Log 'ASD started again'
                schtasks.exe /delete /tn {{UpdateTaskName}} /f | Out-Null
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));   // with a BOM: Windows PowerShell reads other paths wrongly

            var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"";
            if (command.Length > 250) return false;   // schtasks accepts at most 261 characters

            if (RunHidden("schtasks.exe", $"/create /tn {UpdateTaskName} /tr \"{command.Replace("\"", "\\\"")}\" /sc once /st 23:59 /f") != 0) return false;
            if (RunHidden("schtasks.exe", $"/run /tn {UpdateTaskName}") != 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int RunHidden(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start " + file);
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } return -1; }
        return process.ExitCode;
    }

    /// <summary>
    /// If the last silent update could not be installed, the reason (once); the old ASD was opened again.
    /// </summary>
    public static string? TakeFailedUpdateMessage()
    {
        try
        {
            if (!File.Exists(FailedFile)) return null;
            var text = File.ReadAllText(FailedFile).Trim();
            TryDeleteFile(FailedFile);
            return string.IsNullOrEmpty(text) ? "Windows could not install the update." : text;
        }
        catch { return null; }
    }

    /// <summary>
    /// The fallback: opens the downloaded package with Windows' own installer ("App Installer"): it shows an Update button, closes ASD
    /// if it is still open, installs the new version in one step and can start it again. The caller exits ASD right after.
    /// </summary>
    public static void StartMsixInstall()
    {
        if (!File.Exists(MsixPath))
            throw new FileNotFoundException("The downloaded update could not be found.");
        Process.Start(new ProcessStartInfo(MsixPath) { UseShellExecute = true });
    }

    /// <summary>
    /// update.txt records which version is installed; the installer copies it last. The copy inside ASD.zip is used when
    /// there is one: it always belongs to the zip that was downloaded, even if a newer build was published meanwhile.
    /// Only for an older zip without it, the release's update.txt is downloaded (the update fails when that can't be fetched).
    /// </summary>
    private static async Task StageInfoAsync(CancellationToken ct)
    {
        var inZip = Path.Combine(StagingDir, "update.txt");
        if (File.Exists(inZip))
        {
            var packed = await File.ReadAllTextAsync(inZip, ct);
            if (packed.Length <= MaxInfoBytes && packed.Contains("SHA:", StringComparison.Ordinal)) return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            var text = await Http.GetStringAsync(RemoteInfoUrl, timeout.Token);
            if (text.Length > MaxInfoBytes || !text.Contains("SHA:", StringComparison.Ordinal))
                throw new InvalidDataException("The update information on GitHub is not valid.");
            await File.WriteAllTextAsync(Path.Combine(StagingDir, "update.txt"), text, new UTF8Encoding(false), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub did not answer in time. Check your internet connection.");
        }
    }

    /// <summary>
    /// 404 = the release "latest" is being rebuilt right now (each push deletes and recreates it; the files come back a few minutes later).
    /// That is not an error of ASD or of the person's connection, so it gets its own message.
    /// </summary>
    private static void EnsureReleaseFile(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("A new version is being published on GitHub right now. This takes a few minutes: please try again shortly.");
        response.EnsureSuccessStatusCode();
    }

    private static async Task DownloadAsync(string url, string targetPath, IProgress<UpdateProgress> progress, CancellationToken ct)
    {
        var part = targetPath + ".part";
        progress.Report(new UpdateProgress("download", 0, true, 0, 0, 0));

        try
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerTimeout.CancelAfter(ConnectTimeout);

            long existing = File.Exists(part) ? new FileInfo(part).Length : 0;   // an interrupted download continues from here
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDeleteFile(part);   // the saved part does not fit: start again
                await DownloadAsync(url, targetPath, progress, ct);
                return;
            }
            EnsureReleaseFile(response);
            bool resumed = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed) existing = 0;

            long total = response.Content.Headers.ContentLength is long length ? length + existing : -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var target = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = existing;
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
                        var speed = (received - existing) / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                        progress.Report(new UpdateProgress("download",
                            total > 0 ? received * 100.0 / total : 0, total <= 0, received, total, speed));
                    }
                }

                if (total > 0 && received != total)
                    throw new IOException($"The download was incomplete ({received:N0} of {total:N0} bytes).");

                progress.Report(new UpdateProgress("download", 100, false, received, Math.Max(total, received),
                    (received - existing) / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
            }

            File.Move(part, targetPath, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // the .part file stays: the next try continues from it
            throw new TimeoutException("The download stalled. Check your internet connection and try again: it continues from where it stopped.");
        }
        catch
        {
            throw;   // the .part file stays: the next try continues from it
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

        // The package must contain the app itself. (update.txt is normally inside the zip too; StageInfoAsync downloads it otherwise.)
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "ASD.exe");
        if (!File.Exists(Path.Combine(StagingDir, exeName)))
            throw new InvalidDataException($"The update package is invalid ({exeName} is missing).");
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
