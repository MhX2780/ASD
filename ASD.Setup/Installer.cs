using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ASD.Setup;

/// <summary>An error whose message is written for the person using the installer.</summary>
internal sealed class SetupException(string message) : Exception(message);

/// <param name="Version">1.0.N.0</param>
/// <param name="Sha256">hash of ASD.msix</param>
/// <param name="Text">the whole msix.txt</param>
internal readonly record struct RemoteInfo(string Version, string Sha256, string Text);

/// <summary>Everything the installer does: find what is installed, get ASD.msix, trust the publisher, install, start.</summary>
internal static class Installer
{
    /// <summary>The package name in Package.appxmanifest (Identity Name).</summary>
    public const string PackageName = "AdvaASD.ASD";
    public const string BaseUrl = "https://github.com/MhX2780/ASD/releases/download/latest/";

    private static string WorkDir => Path.Combine(Path.GetTempPath(), "ASD-Setup");

    /// <summary>A plain text log of what the installer did (default: %TEMP%\ASD-Setup\setup.log, or --log FILE).</summary>
    public static string LogFile { get; set; } = Path.Combine(Path.GetTempPath(), "ASD-Setup", "setup.log");

    /// <summary>The folder whose ASD.msix / Extensions are used. Normally the folder of this exe; after a rename-relaunch (see App) it is the original folder.</summary>
    public static string? SourceFolder { get; set; }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* a log must never break the install */ }
    }

    // ───────── what is here ─────────

    /// <summary>
    /// The folders that are searched for ASD.msix and Extensions, in this order: Components\ next to this installer (also written
    /// "Compontes"), then the folder of the installer itself (the old layout).
    /// </summary>
    public static IEnumerable<string> ComponentFolders()
    {
        var baseDir = SourceFolder ?? Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(baseDir)) yield break;
        yield return Path.Combine(baseDir, "Components");
        yield return Path.Combine(baseDir, "Compontes");
        yield return baseDir;
    }

    /// <summary>True when the offline ASD.msix is in the Components folder (not just next to the installer).</summary>
    public static bool IsInComponents(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return name.Equals("Components", StringComparison.OrdinalIgnoreCase) || name.Equals("Compontes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ASD.msix in the Components folder (or next to this installer: a USB stick, a CD): then nothing is downloaded.</summary>
    public static string? LocalPackage()
    {
        foreach (var folder in ComponentFolders())
        {
            var path = Path.Combine(folder, "ASD.msix");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>How many extensions the last install put into ASD from the Components folder.</summary>
    public static int ExtensionsInstalled { get; private set; }

    private static System.Version? ReadExtensionVersion(string manifest)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && System.Version.TryParse(v.GetString(), out var r) ? r : null;
        }
        catch { return null; }
    }

    /// <summary>The Extensions folder to use: Components\Extensions (or Extensions next to the installer). It may hold one folder per extension, or an extensions.zip that is unpacked first.</summary>
    private static string? ExtensionsSource()
    {
        foreach (var (folder, name) in ComponentFolders().SelectMany(f => new[] { "Extensions", "Libs" }.Select(n => (f, n))))
        {
            var dir = Path.Combine(folder, name);
            if (!Directory.Exists(dir)) continue;
            bool hasFolders = Directory.GetDirectories(dir).Any(d => File.Exists(Path.Combine(d, "extension.json")));
            if (hasFolders || File.Exists(Path.Combine(dir, "extensions.txt"))) return dir;

            var zip = Path.Combine(dir, "extensions.zip");
            if (File.Exists(zip))
            {
                try
                {
                    var unpacked = Path.Combine(WorkDir, "extensions-unpacked");
                    if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(zip, unpacked, overwriteFiles: true);
                    return unpacked;
                }
                catch (Exception e) { Log("could not unpack extensions.zip: " + e.Message); }
            }
        }
        return null;
    }

    /// <summary>Installs every extension folder found in <paramref name="source"/> into ASD's installed-extensions folder (a newer or equal installed copy is kept).</summary>
    internal static void InstallExtensionsFrom(string source, ISet<string>? only = null, IProgress<(double Fraction, string Text)>? progress = null)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD", "extensions", "installed");
        int count = 0, failed = 0, handled = 0;
        var total = Math.Max(1, only?.Count ?? Directory.GetDirectories(source).Length);
        foreach (var dir in Directory.GetDirectories(source))
        {
            var manifest = Path.Combine(dir, "extension.json");
            if (!File.Exists(manifest)) continue;
            var id = Path.GetFileName(dir);
            if (only != null && !only.Contains(id)) continue;
            progress?.Report((handled++ / (double)total, "Installing " + id + "…"));
            try
            {
                var target = Path.Combine(root, id);
                var have = ReadExtensionVersion(Path.Combine(target, "extension.json"));
                var offered = ReadExtensionVersion(manifest);
                if (have != null && offered != null && have >= offered) { Log($"extension {id}: version {have} is already installed"); continue; }

                var fresh = target + ".new";
                if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
                foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(fresh, Path.GetRelativePath(dir, d)));
                Directory.CreateDirectory(fresh);
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.Copy(f, Path.Combine(fresh, Path.GetRelativePath(dir, f)), overwrite: true);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(root);
                Directory.Move(fresh, target);
                count++;
                Log($"extension {id} installed ({offered?.ToString() ?? "?"})");
            }
            catch (Exception e) { failed++; Log($"could not install the extension {id}: {e.Message}"); }
        }
        progress?.Report((1, "Done."));
        ExtensionsInstalled = count;
        ExtensionsFailed = failed;
    }

    /// <summary>How many extensions could not be installed by the last install.</summary>
    public static int ExtensionsFailed { get; private set; }

    /// <summary>One extension found in the Components folder.</summary>
    internal sealed record BundledExtension(string Id, string Name, string Version, string Description, string Dir);

    /// <summary>The extensions in Components\Extensions (name, version, description from each extension.json), for the Extensions step.</summary>
    internal static List<BundledExtension> FindBundledExtensions()
    {
        var list = new List<BundledExtension>();
        try
        {
            var source = ExtensionsSource();
            if (source == null) return list;
            foreach (var dir in Directory.GetDirectories(source).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var manifest = Path.Combine(dir, "extension.json");
                if (!File.Exists(manifest)) continue;
                string name = "", version = "", description = "";
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    string S(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                    name = S("name");
                    version = S("version");
                    description = S("description");
                }
                catch { /* a damaged manifest: the folder name is shown */ }
                var id = Path.GetFileName(dir);
                list.Add(new BundledExtension(id, name.Length > 0 ? name : id, version, description, dir));
            }
        }
        catch (Exception e) { Log("could not read the extensions in Components: " + e.Message); }
        return list;
    }

    /// <summary>Installs the chosen extensions (all of them came from the same folder).</summary>
    internal static void InstallBundled(IReadOnlyList<BundledExtension> items, IProgress<(double Fraction, string Text)>? progress = null)
    {
        if (items.Count == 0) return;
        var chosen = items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InstallExtensionsFrom(Path.GetDirectoryName(items[0].Dir)!, chosen, progress);
    }

    /// <summary>Installs every extension found in Components (silent mode: nobody chooses).</summary>
    public static void InstallAllBundled()
    {
        var source = ExtensionsSource();
        if (source != null) InstallExtensionsFrom(source);
    }

    /// <summary>
    /// The Extensions\ folder inside Components\ (one folder per extension, with extensions.txt) is copied to
    /// %LocalAppData%\ASD\extensions-offline, where the installed ASD's store looks for it (so any of them can be installed later, offline).
    /// </summary>
    public static int CopyOfflineExtensions()
    {
        try
        {
            var source = ExtensionsSource();
            if (source == null || !File.Exists(Path.Combine(source, "extensions.txt"))) return 0;   // the list for the store is copied only when there is one

            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD", "extensions-offline");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);

            var count = Directory.GetDirectories(target).Length;
            Log($"offline extensions copied: {count} extension(s) -> {target}");
            return count;
        }
        catch (Exception e)
        {
            Log("could not copy the offline extensions: " + e.Message);
            return 0;
        }
    }

    /// <summary>The installed ASD (for the current user), or null.</summary>
    public static Package? FindInstalled()
    {
        try { return new PackageManager().FindPackagesForUser("").FirstOrDefault(p => p.Id.Name == PackageName); }
        catch { return null; }
    }

    public static Version VersionOf(Package package)
    {
        var v = package.Id.Version;
        return new Version(v.Major, v.Minor, v.Build, v.Revision);
    }

    public static string ReadText(string resourceName)
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[]? ReadCertificate() => ReadBytes("ASD.cer");

    internal static byte[]? ReadBytes(string resourceName)
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// Makes .automation files open in ASD with a double-click, and gives them ASD's icon. Written for the current user only
    /// (HKCU\Software\Classes: no administrator permission). The package itself already offers ASD in "Open with"; this makes it
    /// the program used for a double-click when the person has not chosen another one. The command goes through the
    /// asd.exe alias, because the exe inside the package can't be started by path.
    /// </summary>
    public static void RegisterFileType()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var alias = Path.Combine(local, "Microsoft", "WindowsApps", "asd.exe");

            var icon = Path.Combine(local, "ASD", "automation.ico");
            var bytes = ReadBytes("app.ico");
            if (bytes != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
                File.WriteAllBytes(icon, bytes);
            }

            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes")
                ?? throw new InvalidOperationException("HKCU\\Software\\Classes could not be opened.");
            using (var extension = classes.CreateSubKey(".automation"))
                extension.SetValue("", "ASD.Automation.File");
            using (var program = classes.CreateSubKey("ASD.Automation.File"))
            {
                program.SetValue("", "ASD Automation");
                if (bytes != null) { using var key = program.CreateSubKey("DefaultIcon"); key.SetValue("", icon); }
                using var command = program.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{alias}\" \"%1\"");
            }

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED: Explorer reloads the icons
            Log("the .automation file type is registered for this user");
        }
        catch (Exception e)
        {
            Log("could not register the .automation file type: " + e.Message);
        }
    }

    public static void CheckWindows()
    {
        // MSIX needs Windows 10 version 1809 (build 17763) or newer
        if (Environment.OSVersion.Version.Build < 17763)
            throw new SetupException("ASD needs Windows 10 (version 1809) or newer.");
    }

    // ───────── GitHub ─────────

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ASD-Setup");
        return http;
    }

    /// <summary>Reads msix.txt (version + hash) from the release. Throws when it can't.</summary>
    public static async Task<RemoteInfo> FetchRemoteAsync(HttpClient http, CancellationToken ct)
    {
        var text = await http.GetStringAsync(BaseUrl + "msix.txt", ct);
        string? version = null, hash = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) version = line["Version:".Length..].Trim();
            else if (line.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) hash = line["SHA256:".Length..].Trim();
        }
        if (!Version.TryParse(version, out _) || hash is not { Length: 64 })
            throw new InvalidDataException("The version information is not valid.");
        return new RemoteInfo(version!, hash.ToLowerInvariant(), text);
    }

    /// <summary>The newest version on GitHub, or null when it can't be reached (offline).</summary>
    public static async Task<Version?> TryLatestVersionAsync()
    {
        try
        {
            using var http = NewClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var info = await FetchRemoteAsync(http, cts.Token);
            return Version.Parse(info.Version);
        }
        catch { return null; }
    }

    /// <summary>The connection broke before the whole file arrived.</summary>
    private sealed class ConnectionLostException() : Exception("The connection was lost.");

    /// <summary>How many times the download reconnects after the connection is lost, before it gives up.</summary>
    private const int MaxReconnects = 10;

    private static bool IsConnectionProblem(Exception e) => e switch
    {
        ConnectionLostException => true,
        HttpRequestException h => h.StatusCode is null || (int)h.StatusCode >= 500 || (int)h.StatusCode == 408 || (int)h.StatusCode == 429,
        IOException => true,
        OperationCanceledException => true,   // a stall (the caller's own cancel is excluded by the caller)
        _ => false,
    };

    /// <summary>
    /// Downloads ASD.msix and checks it against the hash in msix.txt. Returns the file path.
    /// When the connection breaks, the progress turns to "unknown" (no percent) and the download reconnects up to 10 times,
    /// each time continuing from the part already saved. If it never gets through, it throws and Setup shows the error page.
    /// </summary>
    public static async Task<string> DownloadAsync(IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        var partFile = Path.Combine(WorkDir, "ASD.msix.part");
        long PartSize() => File.Exists(partFile) ? new FileInfo(partFile).Length : 0;

        int failures = 0;
        long lastSize = PartSize();
        while (true)
        {
            try
            {
                return await DownloadOnceAsync(progress, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && IsConnectionProblem(e))
            {
                var size = PartSize();
                if (size > lastSize) { failures = 0; lastSize = size; }   // it moved forward before it broke: count again from zero
                failures++;
                Log($"connection problem ({failures} of {MaxReconnects}): {e.GetType().Name}: {e.Message}");
                if (failures > MaxReconnects)
                    throw new SetupException("ASD could not be downloaded. Check your internet connection, then try again.");
                progress.Report((-1, $"Connection lost. Connecting again… ({failures} of {MaxReconnects})"));
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static async Task<string> DownloadOnceAsync(IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        var target = Path.Combine(WorkDir, "ASD.msix");
        var part = target + ".part";

        try
        {
            using var http = NewClient();
            RemoteInfo info;
            using (var infoTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                infoTimeout.CancelAfter(TimeSpan.FromSeconds(30));   // no answer in 30 s counts as a lost connection
                info = await FetchRemoteAsync(http, infoTimeout.Token);
            }

            long have = File.Exists(part) ? new FileInfo(part).Length : 0;   // a download that was interrupted continues from here
            using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "ASD.msix"))
            {
                if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);

                if (have > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Log("the saved part is already complete: checking it");
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    bool resumed = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    if (!resumed) have = 0;
                    long length = response.Content.Headers.ContentLength ?? 0;
                    long total = length > 0 ? length + have : 0;
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[81920];
                    long done = have, lastReport = 0;
                    var clock = Stopwatch.StartNew();
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);   // a download that stops moving for 30 s is stopped (and can be continued)
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(30));
                        int read = await source.ReadAsync(buffer, idle.Token);
                        if (read <= 0) break;
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        if (clock.ElapsedMilliseconds - lastReport < 120) continue;
                        lastReport = clock.ElapsedMilliseconds;
                        var speed = (done - have) / Math.Max(1, clock.Elapsed.TotalSeconds);
                        progress.Report((total > 0 ? (double)done / total : -1,
                            $"{Mb(done)} of {(total > 0 ? Mb(total) : "?")}  ·  {Mb((long)speed)}/s"));
                    }
                    if (total > 0 && done != total)
                        throw new ConnectionLostException();
                }
            }

            string actual;
            await using (var check = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
            if (actual != info.Sha256)
            {
                try { File.Delete(part); } catch { }   // a bad file must not be continued
                throw new SetupException("The downloaded file is damaged or incomplete. Please try again.");
            }

            File.Move(part, target, overwrite: true);
            return target;
        }
        catch (InvalidDataException e)
        {
            throw new SetupException(e.Message);
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.#} MB";

    // ───────── the publisher certificate ─────────

    /// <summary>Is the package's certificate already trusted on this PC (Local Machine, Trusted People)?</summary>
    public static bool IsTrusted(byte[] certificate)
    {
        try
        {
            using var cert = new X509Certificate2(certificate);
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Windows installs a package only from a publisher it trusts. This adds ASD's own certificate to
    /// Trusted People once (Windows asks for administrator permission) - the one and only thing here that needs it.
    /// </summary>
    public static async Task TrustAsync(byte[] certificate, CancellationToken ct)
    {
        if (IsTrusted(certificate)) return;

        Directory.CreateDirectory(WorkDir);
        var file = Path.Combine(WorkDir, "ASD.cer");
        await File.WriteAllBytesAsync(file, certificate, ct);

        try
        {
            var start = new ProcessStartInfo("certutil.exe", $"-addstore -f TrustedPeople \"{file}\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(start) ?? throw new SetupException("Windows did not start the certificate tool.");
            await process.WaitForExitAsync(ct);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new SetupException("Permission was not given. ASD needs it once, so that Windows will install and update it. Press Install again and choose Yes.");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }

        if (!IsTrusted(certificate))
            throw new SetupException("The publisher certificate could not be added. Try again and choose Yes when Windows asks for permission.");
    }

    // ───────── install and start ─────────

    public static async Task InstallAsync(string msixPath, IProgress<(double Fraction, string Text)> progress)
    {
        try
        {
            await AddAndWaitAsync(msixPath, progress);
        }
        catch (SetupException) { throw; }
        catch (Exception e)
        {
            throw Explain(e.Message, e.HResult);
        }
    }

    /// <summary>
    /// Adds the package and waits for Windows. Windows can finish so fast that the "done" report never reaches us (the window then
    /// stays on "Installing" for ever): so while waiting, the installed version is looked at too, and the wait has a limit.
    /// </summary>
    private static async Task AddAndWaitAsync(string msixPath, IProgress<(double Fraction, string Text)> progress, bool allowRemove = true)
    {
        var expected = ReadPackageVersion(msixPath);
        Version? before = FindInstalled() is { } old ? VersionOf(old) : null;
        Log($"installing {msixPath} (package version {expected?.ToString() ?? "?"}, installed now {before?.ToString() ?? "none"})");

        DateTime? full = null;   // when Windows reported 100 %
        var manager = new PackageManager();
        var operation = manager.AddPackageAsync(
            new Uri(msixPath), null,
            DeploymentOptions.ForceUpdateFromAnyVersion | DeploymentOptions.ForceApplicationShutdown);
        operation.Progress = (_, p) =>
        {
            if (p.percentage >= 100) full ??= DateTime.UtcNow;
            progress.Report((p.percentage / 100.0, "Windows is installing ASD…"));
        };

        var task = Task.Run(async () => await operation);
        var started = DateTime.UtcNow;
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(1000));
            if (task.IsCompleted) break;

            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            var installed = FindInstalled();
            bool newer = expected != null && installed != null && VersionOf(installed) >= expected && (before == null || before < expected);
            bool allDone = full != null && (DateTime.UtcNow - full.Value).TotalSeconds > 6;
            if ((newer && seconds > 3) || allDone)
            {
                Log("Windows installed the package but did not report the end: going on");
                progress.Report((1, "Installed."));
                return;
            }
            if (seconds > 600) throw new SetupException("Windows is taking too long to install ASD. Close this window and run Setup again.");
        }

        var result = await task;
        if (!string.IsNullOrEmpty(result.ErrorText) || result.ExtendedErrorCode != null)
        {
            var code = result.ExtendedErrorCode?.HResult ?? 0;
            if (allowRemove && (uint)code == 0x80073CFB)
            {
                // "already installed, reinstalling is blocked" (a repair of the same version): remove it, keep the data, add it again
                Log("the same version is already installed: removing it first (the data is kept)");
                await RemoveAsync(progress, deleteData: false);
                await AddAndWaitAsync(msixPath, progress, allowRemove: false);
                return;
            }
            throw Explain(result.ErrorText, code);
        }
    }

    /// <summary>The version written in the package's own AppxManifest.xml (the msix is a zip), or null.</summary>
    private static Version? ReadPackageVersion(string msixPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(msixPath);
            var entry = zip.GetEntry("AppxManifest.xml");
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            var match = System.Text.RegularExpressions.Regex.Match(reader.ReadToEnd(), "<Identity[^>]*?\\sVersion=\"([0-9.]+)\"");
            return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
        }
        catch { return null; }
    }

    private static async Task RemoveAsync(IProgress<(double Fraction, string Text)> progress, bool deleteData)
    {
        var package = FindInstalled();
        if (package == null) return;

        var manager = new PackageManager();
        var operation = manager.RemovePackageAsync(package.Id.FullName, deleteData ? RemovalOptions.None : RemovalOptions.PreserveApplicationData);
        operation.Progress = (_, p) => progress.Report((p.percentage / 100.0, "Windows is removing ASD…"));

        var task = Task.Run(async () => await operation);
        var started = DateTime.UtcNow;
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(1000));
            if (task.IsCompleted) break;
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            if (seconds > 3 && FindInstalled() == null) { Log("ASD is removed (Windows did not report the end)"); return; }
            if (seconds > 300) throw new SetupException("Windows is taking too long to remove ASD. Close this window and run Setup again.");
        }

        var result = await task;
        if (!string.IsNullOrEmpty(result.ErrorText) || result.ExtendedErrorCode != null)
            throw Explain(result.ErrorText, result.ExtendedErrorCode?.HResult ?? 0);
    }

    /// <summary>Removes ASD from Windows. The data stays unless <paramref name="deleteData"/> (then everything ASD stored is deleted too).</summary>
    public static async Task UninstallAsync(bool deleteData, IProgress<(double Fraction, string Text)> progress)
    {
        if (FindInstalled() == null) throw new SetupException("ASD is not installed.");
        try
        {
            await RemoveAsync(progress, deleteData);
        }
        catch (SetupException) { throw; }
        catch (Exception e)
        {
            throw Explain(e.Message, e.HResult);
        }
        progress.Report((1, "Cleaning up…"));
        await Task.Run(() => UnregisterFileType(deleteData));
    }

    /// <summary>Takes back what RegisterFileType wrote (.automation opens in ASD) and, when asked, deletes ASD's data folder.</summary>
    public static void UnregisterFileType(bool deleteData)
    {
        try
        {
            using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
            {
                classes?.DeleteSubKeyTree(".automation", throwOnMissingSubKey: false);
                classes?.DeleteSubKeyTree("ASD.Automation.File", throwOnMissingSubKey: false);
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD");
            if (deleteData)
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            else
            {
                var icon = Path.Combine(folder, "automation.ico");
                if (File.Exists(icon)) File.Delete(icon);
            }
            Log("the .automation file type was taken back" + (deleteData ? " and ASD's data folder was deleted" : ""));
        }
        catch (Exception e)
        {
            Log("could not clean up after the uninstall: " + e.Message);
        }
    }

    private static SetupException Explain(string? text, int hresult)
    {
        var code = $"0x{(uint)hresult:X8}";
        string message = (uint)hresult switch
        {
            0x800B0109 or 0x800B010A or 0x800B0100 or 0x800B0101 =>
                "Windows does not trust the publisher of this package. Run Setup again and allow the certificate step.",
            0x80073CF9 or 0x80073D02 =>
                "ASD is open or in use. Close ASD and try again.",
            0x80073CFF =>
                "Windows is not set up to install apps from outside the Microsoft Store. Turn on \"Developer Mode\" in Settings, then run Setup again.",
            _ => "Windows could not install ASD.",
        };
        var detail = string.IsNullOrWhiteSpace(text) ? "" : "\n\n" + text.Trim();
        if (detail.Length > 700) detail = detail[..700] + "…";
        return new SetupException($"{message}\n\nError {code}{detail}");
    }

    /// <summary>Starts the installed ASD (through its package: the exe inside it can't be started by path).</summary>
    public static void Launch()
    {
        var installed = FindInstalled();
        if (installed == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{installed.Id.FamilyName}!App") { UseShellExecute = false });
    }
}
