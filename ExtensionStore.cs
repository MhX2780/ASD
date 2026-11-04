using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ASD;

/// <summary>One extension as the Extensions window shows it.</summary>
internal sealed class ExtInfo
{
    public string Id = "";
    public string Version = "0";
    public string Name = "";
    public string Description = "";
    public string Author = "";
    public string Glyph = "";
    public string SimpleIcon = "";
    public string DefaultPlacement = "more";      // "main" = navigation bar, "more" = More tools
    public string MinAsdVersion = "";
    public string AssemblyFile = "";
    public int ApiVersion = 1;
    public string[] RequiresPrograms = Array.Empty<string>();
    public string[] RequiresExtensions = Array.Empty<string>();

    /// <summary>The folder of this extension inside the downloaded package (null when only the list is known, e.g. offline).</summary>
    public string? PackageDir;

    /// <summary>The folder of the installed copy (null when not installed).</summary>
    public string? InstalledDir;
    public string InstalledVersion = "";

    public bool Installed => InstalledDir != null;

    /// <summary>Why ASD did not start this extension (null = fine or not started yet).</summary>
    public string? LoadError => ExtensionLoader.ErrorOf(Id);

    /// <summary>True when this run of ASD has started the extension.</summary>
    public bool Running => ExtensionLoader.IsLoaded(Id);
    public bool IsNew;
    public bool HasUpdate => Installed && ExtensionStore.IsNewer(Version, InstalledVersion);

    public string? IconPath => PackageDir == null ? null : Existing(Path.Combine(PackageDir, "icon.png")) ?? (InstalledDir == null ? null : Existing(Path.Combine(InstalledDir, "icon.png")));
    public string? LicensePath => PackageDir == null ? null : Directory.EnumerateFiles(PackageDir, "LICENSE*").FirstOrDefault();
    public IReadOnlyList<string> Screenshots
    {
        get
        {
            var dir = PackageDir == null ? null : Path.Combine(PackageDir, "screenshots");
            return dir != null && Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList()
                : new List<string>();
        }
    }

    private static string? Existing(string p) => File.Exists(p) ? p : null;
}

internal readonly record struct ExtProgress(string Stage, double Percent, bool Indeterminate, long Received, long Total, double BytesPerSecond);

/// <summary>
/// The extension store (no UI). It reads two files from the "extensions" release of the ASD-Extensions repository:
///   extensions.txt   tiny list: line 1 = SHA-256 of extensions.zip, then  id | version | name | description
///   extensions.zip   one folder per extension (DLL, extension.json, icon.png, LICENSE*, screenshots/)
/// The zip is downloaded only when its hash is new; it is checked against the hash, unpacked into a cache folder,
/// and "Install" copies one extension's folder to the installed folder. Nothing is loaded here.
/// </summary>
internal static class ExtensionStore
{
    public const string ListUrl = "https://github.com/MhX2780/ASD-Extensions/releases/download/extensions/extensions.txt";
    public const string ZipUrl  = "https://github.com/MhX2780/ASD-Extensions/releases/download/extensions/extensions.zip";

    /// <summary>The newest extension API (IAsdHost.ApiVersion) this ASD understands.</summary>
    public const int SupportedApiVersion = 1;

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ASD-Extensions/1.0");
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    // ───────── folders ─────────

    private static string Root => AppPaths.File("extensions");
    private static string CacheDir => Path.Combine(Root, "cache");          // the unpacked package
    public static string InstalledRoot => Path.Combine(Root, "installed");  // one folder per installed extension
    private static string HashFile => Path.Combine(Root, "package-hash.txt");
    private static string ListFile => Path.Combine(Root, "known.txt");      // the last list that was read
    private static string SeenFile => Path.Combine(Root, "seen.txt");       // ids the person has already seen

    /// <summary>True when the list could not be downloaded and the saved one is shown.</summary>
    public static bool Offline { get; private set; }

    // ───────── versions ─────────

    public static bool IsNewer(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var l = i < x.Length ? x[i] : 0; var r = i < y.Length ? y[i] : 0;
            if (l != r) return l > r;
        }
        return false;
    }

    public static Version AsdVersion => typeof(ExtensionStore).Assembly.GetName().Version ?? new Version(1, 0);

    // ───────── reading the list, downloading the package ─────────

    private sealed record ListLine(string Id, string Version, string Name, string Description);

    private static (string Hash, List<ListLine> Lines) ParseList(string text)
    {
        var rows = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var hash = rows.Length > 0 ? rows[0].Trim().ToLowerInvariant() : "";
        var lines = new List<ListLine>();
        foreach (var r in rows.Skip(1))
        {
            var p = r.Split('|').Select(s => s.Trim()).ToArray();
            if (p.Length >= 3 && p[0].Length > 0) lines.Add(new ListLine(p[0], p[1], p[2], p.Length > 3 ? p[3] : ""));
        }
        return (hash, lines);
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.GetAsync(url, cts.Token);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("The extensions list is too large.");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads the list; downloads the package when its hash changed (progress is reported); unpacks it.
    /// With no connection the saved list and package are used (see <see cref="Offline"/>).
    /// </summary>
    public static async Task<List<ExtInfo>> RefreshAsync(IProgress<ExtProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var firstRun = !File.Exists(SeenFile) && !File.Exists(ListFile);
        string? listText = null;
        Offline = false;

        progress?.Report(new ExtProgress("list", 0, true, 0, 0, 0));
        try { listText = await GetTextAsync(ListUrl, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (!File.Exists(ListFile) || !Directory.Exists(CacheDir))
                throw new InvalidOperationException("Could not reach the extensions list (" + ex.Message + ") and nothing is saved yet.");
            Offline = true;
            listText = File.ReadAllText(ListFile);
        }

        var (hash, lines) = ParseList(listText);
        if (!Offline)
        {
            if (hash.Length != 64) throw new InvalidDataException("The extensions list does not start with a SHA-256 hash.");
            var have = File.Exists(HashFile) ? File.ReadAllText(HashFile).Trim() : "";
            if (have != hash || !Directory.Exists(CacheDir))
            {
                await DownloadPackageAsync(hash, progress, ct);
                File.WriteAllText(HashFile, hash);
            }
            File.WriteAllText(ListFile, listText);
        }

        // first time ever: nothing is "new" yet, everything is simply there
        var seen = File.Exists(SeenFile) ? File.ReadAllLines(SeenFile).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (firstRun) { seen = lines.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); File.WriteAllLines(SeenFile, seen); }

        var result = new List<ExtInfo>();
        foreach (var l in lines)
        {
            var e = new ExtInfo { Id = l.Id, Version = l.Version, Name = l.Name, Description = l.Description };
            var pkg = Path.Combine(CacheDir, l.Id);
            if (Directory.Exists(pkg)) { e.PackageDir = pkg; ReadManifest(e, Path.Combine(pkg, "extension.json")); }
            e.IsNew = !seen.Contains(l.Id);
            result.Add(e);
        }

        // installed copies (also the ones that are no longer in the list)
        foreach (var dir in Directory.Exists(InstalledRoot) ? Directory.GetDirectories(InstalledRoot) : Array.Empty<string>())
        {
            var manifest = Path.Combine(dir, "extension.json");
            if (!File.Exists(manifest)) continue;
            var id = Path.GetFileName(dir);
            var e = result.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var installed = new ExtInfo { Id = id };
            ReadManifest(installed, manifest);
            if (e == null) { e = installed; e.Name = installed.Name.Length > 0 ? installed.Name : id; result.Add(e); }
            e.InstalledDir = dir;
            e.InstalledVersion = installed.Version;
            if (e.Author.Length == 0) e.Author = installed.Author;
        }

        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task DownloadPackageAsync(string expectedHash, IProgress<ExtProgress>? progress, CancellationToken ct)
    {
        var tmpZip = Path.Combine(Root, "extensions.zip.download");
        var tmpDir = Path.Combine(Root, "cache.new");
        try
        {
            using (var resp = await Http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var input = await resp.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(tmpZip);
                var buffer = new byte[81920];
                long got = 0; var sw = Stopwatch.StartNew(); long lastTick = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    got += n;
                    if (sw.ElapsedMilliseconds - lastTick > 80)
                    {
                        lastTick = sw.ElapsedMilliseconds;
                        progress?.Report(new ExtProgress("download", total > 0 ? got * 100.0 / total : 0, total <= 0, got, total, got / Math.Max(0.1, sw.Elapsed.TotalSeconds)));
                    }
                }
                progress?.Report(new ExtProgress("download", 100, false, got, total, got / Math.Max(0.1, sw.Elapsed.TotalSeconds)));
            }

            // the hash in the list must match the file, or the package is refused (nothing is unpacked)
            string actual;
            await using (var fs = File.OpenRead(tmpZip)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != expectedHash) throw new InvalidDataException("The downloaded package does not match its SHA-256 hash, so it was not used.");

            progress?.Report(new ExtProgress("extract", 0, true, 0, 0, 0));
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            await Task.Run(() => SafeExtract(tmpZip, tmpDir), ct);
            if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
            Directory.Move(tmpDir, CacheDir);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    /// <summary>Unpacks a zip, refusing any entry that would land outside the target folder.</summary>
    private static void SafeExtract(string zip, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The package has an unsafe path: " + entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
    }

    private static void ReadManifest(ExtInfo e, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var r = doc.RootElement;
            string S(string n, string d = "") => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
            string[] A(JsonElement parent, string n) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();

            if (S("name").Length > 0) e.Name = S("name");
            if (S("version").Length > 0) e.Version = S("version");
            if (S("description").Length > 0) e.Description = S("description");
            e.Author = S("author", e.Author);
            if (S("glyph").Length > 0) e.Glyph = S("glyph");
            e.SimpleIcon = S("simpleIcon", e.SimpleIcon);
            e.DefaultPlacement = S("defaultPlacement", "more") == "main" ? "main" : "more";
            e.MinAsdVersion = S("minAsdVersion", e.MinAsdVersion);
            e.AssemblyFile = S("assembly", e.AssemblyFile);
            if (r.TryGetProperty("apiVersion", out var api) && api.ValueKind == JsonValueKind.Number) e.ApiVersion = api.GetInt32();
            if (r.TryGetProperty("requires", out var req))
            {
                e.RequiresPrograms = A(req, "programs");
                e.RequiresExtensions = A(req, "extensions");
            }
        }
        catch { /* a damaged manifest: the list line still shows the extension */ }
    }

    public static void MarkSeen(string id)
    {
        try
        {
            var seen = File.Exists(SeenFile) ? File.ReadAllLines(SeenFile).ToList() : new List<string>();
            if (!seen.Contains(id, StringComparer.OrdinalIgnoreCase)) { seen.Add(id); Directory.CreateDirectory(Root); File.WriteAllLines(SeenFile, seen); }
        }
        catch { }
    }

    /// <summary>Quick check used by "checking for a newer version": has the list changed since the package was downloaded?</summary>
    public static async Task<bool> ListChangedAsync(CancellationToken ct)
    {
        var (hash, _) = ParseList(await GetTextAsync(ListUrl, ct));
        var have = File.Exists(HashFile) ? File.ReadAllText(HashFile).Trim() : "";
        return hash.Length == 64 && hash != have;
    }

    // ───────── the badge on the navigation item ─────────

    private static string LatestFile => Path.Combine(Root, "latest.txt");

    /// <summary>
    /// How many extensions are new or have an update, for the badge: reads only the small list (never the zip),
    /// from the internet at most about once a day (or when <paramref name="force"/>), otherwise from the saved copy.
    /// </summary>
    public static async Task<int> QuickCountAsync(bool force, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var due = force || !DateTime.TryParse(SettingsStore.Get("ext.lastcheck", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) || DateTime.UtcNow - last > TimeSpan.FromHours(20);
            if (due)
            {
                try
                {
                    File.WriteAllText(LatestFile, await GetTextAsync(ListUrl, ct));
                    SettingsStore.Set("ext.lastcheck", DateTime.UtcNow.ToString("o"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* no connection: use what is saved */ }
            }
            if (!File.Exists(LatestFile)) return 0;

            var (_, lines) = ParseList(File.ReadAllText(LatestFile));
            var seenKnown = File.Exists(SeenFile);
            var seen = seenKnown ? File.ReadAllLines(SeenFile).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var l in lines)
            {
                var manifest = Path.Combine(InstalledRoot, l.Id, "extension.json");
                if (File.Exists(manifest))
                {
                    var probe = new ExtInfo { Id = l.Id };
                    ReadManifest(probe, manifest);
                    if (IsNewer(l.Version, probe.Version)) count++;
                }
                else if (seenKnown && !seen.Contains(l.Id)) count++;
            }
            return count;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }

    // ───────── placement ─────────

    /// <summary>"main" (navigation bar) or "more" (More tools): the person's choice, or the extension's default.</summary>
    public static string PlacementOf(ExtInfo e) => SettingsStore.Get("ext.place." + e.Id, e.DefaultPlacement) == "main" ? "main" : "more";
    public static void SetPlacement(string id, string placement) => SettingsStore.Set("ext.place." + id, placement == "main" ? "main" : "more");

    // ───────── what an extension needs ─────────

    internal readonly record struct Need(string Text, bool Ok, bool Blocks);

    public static List<Need> CheckNeeds(ExtInfo e, IReadOnlyList<ExtInfo> all)
    {
        var list = new List<Need>();
        if (e.ApiVersion > SupportedApiVersion)
            list.Add(new Need($"Extension API {e.ApiVersion} (this ASD understands up to {SupportedApiVersion}). Update ASD first.", false, true));
        else
            list.Add(new Need($"Extension API {e.ApiVersion}", true, false));

        if (e.MinAsdVersion.Length > 0)
        {
            var ok = !IsNewer(e.MinAsdVersion, AsdVersion.ToString());
            list.Add(new Need($"ASD {e.MinAsdVersion} or newer (this is {AsdVersion.ToString(3)})", ok, !ok));
        }
        foreach (var p in e.RequiresPrograms)
        {
            var found = ProgramOnPath(p);
            list.Add(new Need($"Program \"{p}\" " + (found ? "found" : "was not found on this computer (the extension may not work until it is installed)"), found, false));
        }
        foreach (var x in e.RequiresExtensions)
        {
            var have = all.FirstOrDefault(a => a.Id.Equals(x, StringComparison.OrdinalIgnoreCase));
            var ok = have?.Installed == true;
            list.Add(new Need($"Extension \"{x}\" " + (ok ? "is installed" : have != null ? "is not installed (install it first)" : "is not in the store"), ok, !ok));
        }
        return list;
    }

    private static bool ProgramOnPath(string name)
    {
        try
        {
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var d = dir.Trim().Trim('"');
                if (File.Exists(Path.Combine(d, name))) return true;
                foreach (var x in exts) if (File.Exists(Path.Combine(d, name + x))) return true;
            }
        }
        catch { }
        return false;
    }

    // ───────── what is installed, and changes that wait for a restart ─────────

    private static string PendingRoot => Path.Combine(Root, "pending");
    private static string PendingRemoveFile => Path.Combine(Root, "pending-remove.txt");

    public static List<string> InstalledDirs()
        => Directory.Exists(InstalledRoot) ? Directory.GetDirectories(InstalledRoot).Where(d => !d.EndsWith(".new", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();

    public static ExtInfo ReadInstalled(string dir)
    {
        var e = new ExtInfo { Id = Path.GetFileName(dir), InstalledDir = dir };
        ReadManifest(e, Path.Combine(dir, "extension.json"));
        return e;
    }

    /// <summary>
    /// At start-up, before anything is loaded: finishes the updates and removals that could not be done while the old
    /// DLL was in use (a loaded DLL cannot be replaced or deleted).
    /// </summary>
    public static void ApplyPending()
    {
        try
        {
            if (File.Exists(PendingRemoveFile))
            {
                foreach (var id in File.ReadAllLines(PendingRemoveFile).Where(x => x.Trim().Length > 0))
                {
                    var d = Path.Combine(InstalledRoot, id.Trim());
                    if (Directory.Exists(d)) Directory.Delete(d, true);
                }
                File.Delete(PendingRemoveFile);
            }
            if (Directory.Exists(PendingRoot))
            {
                foreach (var src in Directory.GetDirectories(PendingRoot))
                {
                    var dst = Path.Combine(InstalledRoot, Path.GetFileName(src));
                    Directory.CreateDirectory(InstalledRoot);
                    if (Directory.Exists(dst)) Directory.Delete(dst, true);
                    Directory.Move(src, dst);
                }
            }
        }
        catch { /* try again at the next start */ }
    }

    // ───────── install / remove ─────────

    /// <summary>Copies the extension's folder from the downloaded package to the installed folder (replacing an older copy).</summary>
    public static async Task InstallAsync(ExtInfo e, string placement, IProgress<ExtProgress>? progress, CancellationToken ct)
    {
        if (e.PackageDir == null || !Directory.Exists(e.PackageDir)) throw new InvalidOperationException("The package of this extension is not on this computer yet.");
        var target = Path.Combine(InstalledRoot, e.Id);
        var fresh = target + ".new";
        await Task.Run(() =>
        {
            Directory.CreateDirectory(InstalledRoot);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            var files = Directory.GetFiles(e.PackageDir, "*", SearchOption.AllDirectories);
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(e.PackageDir, files[i]);
                var dest = Path.Combine(fresh, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(files[i], dest, true);
                progress?.Report(new ExtProgress("install", (i + 1) * 100.0 / files.Length, false, i + 1, files.Length, 0));
            }
            if (ExtensionLoader.IsLoaded(e.Id) && Directory.Exists(target))
            {
                // the old DLL is in use right now: keep the new copy aside, ASD swaps them at the next start
                var staged = Path.Combine(PendingRoot, e.Id);
                Directory.CreateDirectory(PendingRoot);
                if (Directory.Exists(staged)) Directory.Delete(staged, true);
                Directory.Move(fresh, staged);
            }
            else
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(fresh, target);
            }
        }, ct);
        SetPlacement(e.Id, placement);
        MarkSeen(e.Id);
        e.InstalledDir = target;
        e.InstalledVersion = e.Version;
    }

    /// <summary>Removes an installed extension. Returns true when ASD must restart to finish (the DLL is in use right now).</summary>
    public static bool Remove(ExtInfo e)
    {
        var dir = e.InstalledDir ?? Path.Combine(InstalledRoot, e.Id);
        var later = ExtensionLoader.IsLoaded(e.Id);
        if (later) File.AppendAllLines(PendingRemoveFile, new[] { e.Id });
        else if (Directory.Exists(dir)) Directory.Delete(dir, true);
        SettingsStore.Set("ext.disabled." + e.Id, "");
        e.InstalledDir = null;
        e.InstalledVersion = "";
        return later;
    }
}
