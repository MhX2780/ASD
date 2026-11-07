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

    /// <summary>ASD.msix placed next to this installer (a USB stick, a CD): then nothing is downloaded.</summary>
    public static string? LocalPackage()
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(folder)) return null;
        var path = Path.Combine(folder, "ASD.msix");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// An Extensions\ folder next to this installer (the offline extensions package: extensions.txt + one folder per extension)
    /// is copied to %LocalAppData%\ASD\extensions-offline, where the installed ASD looks for it. Returns how many extensions were copied.
    /// </summary>
    public static int CopyOfflineExtensions()
    {
        try
        {
            var folder = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(folder)) return 0;
            var source = Path.Combine(folder, "Extensions");
            if (!File.Exists(Path.Combine(source, "extensions.txt"))) return 0;

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
            throw new InvalidDataException("msix.txt on GitHub is not valid.");
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

    /// <summary>Downloads ASD.msix and checks it against the hash in msix.txt. Returns the file path.</summary>
    public static async Task<string> DownloadAsync(IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        var target = Path.Combine(WorkDir, "ASD.msix");
        var part = target + ".part";

        try
        {
            using var http = NewClient();
            var info = await FetchRemoteAsync(http, ct);

            using (var response = await http.GetAsync(BaseUrl + "ASD.msix", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long done = 0, lastReport = 0;
                var clock = Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (clock.ElapsedMilliseconds - lastReport < 120) continue;
                    lastReport = clock.ElapsedMilliseconds;
                    var speed = done / Math.Max(1, clock.Elapsed.TotalSeconds);
                    progress.Report((total > 0 ? (double)done / total : -1,
                        $"{Mb(done)} of {(total > 0 ? Mb(total) : "?")}  ·  {Mb((long)speed)}/s"));
                }
            }

            string actual;
            await using (var check = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
            if (actual != info.Sha256)
                throw new SetupException("The downloaded file is damaged or incomplete. Please try again.");

            File.Move(part, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException e)
        {
            throw new SetupException("ASD could not be downloaded from GitHub. Check your internet connection and try again.\n\n" + e.Message);
        }
        catch (InvalidDataException e)
        {
            throw new SetupException(e.Message);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* leftover is harmless */ }
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
            var manager = new PackageManager();
            var operation = manager.AddPackageAsync(
                new Uri(msixPath), null,
                DeploymentOptions.ForceUpdateFromAnyVersion | DeploymentOptions.ForceApplicationShutdown);
            operation.Progress = (_, p) => progress.Report((p.percentage / 100.0, "Windows is installing ASD…"));

            var result = await operation;
            if (!string.IsNullOrEmpty(result.ErrorText) || result.ExtendedErrorCode != null)
                throw Explain(result.ErrorText, result.ExtendedErrorCode?.HResult ?? 0);
        }
        catch (SetupException) { throw; }
        catch (Exception e)
        {
            throw Explain(e.Message, e.HResult);
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
