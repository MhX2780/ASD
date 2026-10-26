using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ASD;

public sealed class HttpDownloadOptions
{
    public string Url = "";
    public string Folder = "";
    public string? FileName;
    public int Connections = 8;
    public long SpeedLimitBps;              // 0 = unlimited
    public List<(string Name, string Value)> Headers = new();
    public bool Resume = true, Overwrite, Insecure;
    public string? Sha256;
}

/// <summary>One byte range of the file, downloaded by one connection.</summary>
public sealed class DownloadChunk
{
    public int Index;
    public long Start, End = -1;            // End = -1 → length unknown
    public long Done;                       // bytes already written (relative to Start)
    public int Retries;
    public volatile string State = "queued";
    public double Speed;
    internal long LastDone;
    internal double LastT;
    public long Length => End >= 0 ? End - Start + 1 : -1;
}

internal sealed record ChunkState(long Start, long End, long Done);
internal sealed record ResumeState(string Url, long Size, string? ETag, string? LastModified, List<ChunkState> Chunks);

/// <summary>Keeps downloads alive while the user switches between tools.</summary>
public static class HttpDownloads
{
    public static readonly List<HttpDownloadJob> Jobs = new();
    private static int _next;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);

    public static HttpDownloadJob Add(HttpDownloadOptions o)
    {
        var job = new HttpDownloadJob(++_next, o);
        Jobs.Add(job);
        return job;
    }

    public static void Remove(HttpDownloadJob job) => Jobs.Remove(job);

    internal static bool TryReserve(string path) { lock (Reserved) return Reserved.Add(path); }
    internal static void Release(string path) { lock (Reserved) Reserved.Remove(path); }
}

public sealed class HttpDownloadJob
{
    private const int MaxRetries = 6;

    public readonly int Id;
    public readonly HttpDownloadOptions Opt;
    public readonly List<DownloadChunk> Chunks = new();

    public string Phase = "Queued";         // Queued · Probing · Downloading · Verifying · Paused · Done · Failed · Cancelled
    public string? Error;
    public string LastEvent = "";
    public long TotalSize = -1;
    public bool AcceptRanges, Resumed;
    public string? ETag, LastModified, ContentType, Server, FinalUrl;
    public string FileName = "", DestPath = "", PartPath = "", StatePath = "";
    public double Speed, AvgSpeed;
    public TimeSpan? Eta;

    private bool _keepOnCancel;
    private bool _planned, _metaRead;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _limit;
    private long _throttled;
    private long _startBytes;
    private TimeSpan _baseElapsed;
    private readonly Stopwatch _runClock = new();
    private readonly Stopwatch _throttleClock = new();
    private readonly Queue<(double T, long Bytes)> _window = new();

    public HttpDownloadJob(int id, HttpDownloadOptions opt)
    {
        Id = id;
        Opt = opt;
        _limit = opt.SpeedLimitBps;
        FileName = opt.FileName ?? "";
    }

    public long LimitBps { get => Volatile.Read(ref _limit); set => Volatile.Write(ref _limit, value); }
    public long Downloaded => Chunks.Sum(c => Volatile.Read(ref c.Done));
    public bool IsRunning => _task is { IsCompleted: false };
    public bool Finished => Phase is "Done" or "Cancelled";
    public TimeSpan Elapsed => _baseElapsed + _runClock.Elapsed;
    private string UrlToUse => FinalUrl ?? Opt.Url;

    private void Note(string text) => LastEvent = text;

    // ───────── control ─────────

    public void Start()
    {
        if (IsRunning || Finished) return;
        _keepOnCancel = true;
        _cts = new CancellationTokenSource();
        Error = null;
        foreach (var c in Chunks) { c.Retries = 0; if (c.State != "done") c.State = "queued"; }
        var token = _cts.Token;
        _task = Task.Run(() => RunAsync(token));
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _keepOnCancel = true;
        _cts?.Cancel();
    }

    public void Cancel()
    {
        _keepOnCancel = false;
        if (IsRunning) { _cts?.Cancel(); return; }
        CleanupFiles();
        Phase = "Cancelled";
        Release();
    }

    // ───────── main ─────────

    private async Task RunAsync(CancellationToken ct)
    {
        SafeFileHandle? fh = null;
        HttpClient? http = null;
        _runClock.Restart();
        _throttleClock.Restart();
        _throttled = 0;
        _window.Clear();
        try
        {
            http = CreateClient();
            if (!_planned)
            {
                Phase = "Probing";
                Note("Contacting the server …");
                await ProbeAsync(http, ct);
                PlanPaths();
                Plan();
                _planned = true;
            }

            // A server without range support cannot continue a partial file.
            if (!(AcceptRanges && TotalSize > 0))
                foreach (var c in Chunks) Volatile.Write(ref c.Done, 0);

            _startBytes = Downloaded;
            fh = Downloaded > 0 && File.Exists(PartPath)
                ? File.OpenHandle(PartPath, FileMode.Open, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous)
                : File.OpenHandle(PartPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous, TotalSize > 0 ? TotalSize : 0);

            Phase = "Downloading";
            Note(Resumed ? $"Resuming from {SizeFmt.Bytes(Downloaded)}" : $"Downloading with {Chunks.Count} connection(s)");

            using var saverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(async () =>
            {
                try { while (true) { await Task.Delay(2000, saverCts.Token); SaveState(); } }
                catch { /* stopped */ }
            });

            using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = Chunks.Where(c => c.Length < 0 || Volatile.Read(ref c.Done) < c.Length).ToList();
            var tasks = pending.Select(async c =>
            {
                try { await RunChunkAsync(http, c, fh, work.Token); }
                catch { work.Cancel(); throw; }
            }).ToList();

            try { await Task.WhenAll(tasks); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                var real = tasks.Where(t => t.IsFaulted)
                                .Select(t => t.Exception!.GetBaseException())
                                .FirstOrDefault(e => e is not OperationCanceledException);
                throw real ?? new IOException("The download failed.");
            }
            finally { saverCts.Cancel(); }

            fh.Dispose();
            fh = null;

            if (!string.IsNullOrWhiteSpace(Opt.Sha256))
            {
                Phase = "Verifying";
                Note("Checking SHA-256 …");
                await using var fs = new FileStream(PartPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var hex = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hex.Equals(Opt.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 mismatch. Expected {Opt.Sha256.Trim()} but the file is {hex}.");
            }

            File.Move(PartPath, DestPath, Opt.Overwrite);
            TryDelete(StatePath);
            Phase = "Done";
            Note($"Saved to {DestPath}");
            Release();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SaveState();
            if (_keepOnCancel) { Phase = "Paused"; Note("Paused — progress is saved, press Resume to continue."); }
            else { fh?.Dispose(); fh = null; CleanupFiles(); Phase = "Cancelled"; Note("Cancelled."); Release(); }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Phase = "Failed";
            Note("Failed: " + ex.Message);
            SaveState();
        }
        finally
        {
            fh?.Dispose();
            http?.Dispose();
            _baseElapsed += _runClock.Elapsed;
            _runClock.Reset();
            Speed = 0;
            Eta = null;
        }
    }

    // ───────── HTTP ─────────

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,   // byte ranges must match the file exactly
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = Math.Max(4, Opt.Connections + 2),
        };
        if (Opt.Insecure)
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true };

        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ASD-Downloader/1.0");
        return http;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        foreach (var (name, value) in Opt.Headers)
        {
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.Remove(name);
            req.Headers.TryAddWithoutValidation(name, value);
        }
        return req;
    }

    private async Task ProbeAsync(HttpClient http, CancellationToken ct)
    {
        var haveSize = false;
        var ranges = false;

        try
        {
            using var req = NewRequest(HttpMethod.Head, Opt.Url);
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (r.IsSuccessStatusCode)
            {
                ReadMeta(r);
                TotalSize = r.Content.Headers.ContentLength ?? -1;
                haveSize = TotalSize > 0;
                ranges = r.Headers.AcceptRanges.Contains("bytes");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* some servers reject HEAD: the range probe below still works */ }

        if (haveSize && ranges) { AcceptRanges = true; return; }

        using var greq = NewRequest(HttpMethod.Get, UrlToUse);
        greq.Headers.Range = new RangeHeaderValue(0, 0);
        using var gr = await http.SendAsync(greq, HttpCompletionOption.ResponseHeadersRead, ct);
        gr.EnsureSuccessStatusCode();
        if (!_metaRead) ReadMeta(gr);

        if (gr.StatusCode == HttpStatusCode.PartialContent)
        {
            AcceptRanges = true;
            TotalSize = gr.Content.Headers.ContentRange?.Length ?? TotalSize;
        }
        else
        {
            AcceptRanges = false;
            TotalSize = gr.Content.Headers.ContentLength ?? TotalSize;
        }
    }

    private void ReadMeta(HttpResponseMessage r)
    {
        _metaRead = true;
        FinalUrl = r.RequestMessage?.RequestUri?.ToString() ?? Opt.Url;
        ETag = r.Headers.ETag?.ToString();
        LastModified = r.Content.Headers.LastModified?.ToString("R");
        ContentType = r.Content.Headers.ContentType?.MediaType;
        Server = r.Headers.Server.ToString();

        var cd = r.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Path.GetFileName(Uri.UnescapeDataString(new Uri(FinalUrl).AbsolutePath)); }
            catch { name = null; }
        }
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        if (string.IsNullOrWhiteSpace(Opt.FileName)) FileName = name;
    }

    // ───────── planning ─────────

    private void PlanPaths()
    {
        Directory.CreateDirectory(Opt.Folder);
        var name = Sanitize(string.IsNullOrWhiteSpace(Opt.FileName) ? FileName : Opt.FileName!);
        var path = Path.Combine(Opt.Folder, name);
        if (!Opt.Overwrite) path = Unique(path);
        else HttpDownloads.TryReserve(path);

        DestPath = path;
        FileName = Path.GetFileName(path);
        PartPath = path + ".part";
        StatePath = path + ".asd-resume.json";
    }

    private string Unique(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidate = path;
        for (var n = 1; ; n++)
        {
            var free = !File.Exists(candidate)
                       && (!File.Exists(candidate + ".part") || (Opt.Resume && File.Exists(candidate + ".asd-resume.json")));
            if (free && HttpDownloads.TryReserve(candidate)) return candidate;
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
        }
    }

    private void Release() { if (DestPath.Length > 0) HttpDownloads.Release(DestPath); }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "download" : name;
    }

    private void Plan()
    {
        if (Opt.Resume && TotalSize > 0 && AcceptRanges && File.Exists(StatePath) && File.Exists(PartPath))
        {
            try
            {
                var st = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(StatePath));
                if (st != null && st.Size == TotalSize && st.ETag == ETag && st.LastModified == LastModified
                    && st.Chunks.Count > 0 && new FileInfo(PartPath).Length == TotalSize)
                {
                    for (var i = 0; i < st.Chunks.Count; i++)
                    {
                        var c = st.Chunks[i];
                        Chunks.Add(new DownloadChunk { Index = i, Start = c.Start, End = c.End, Done = Math.Clamp(c.Done, 0, c.End - c.Start + 1) });
                    }
                    Resumed = Downloaded > 0;
                    return;
                }
            }
            catch { /* unreadable state: start over */ }
        }

        if (TotalSize > 0 && AcceptRanges && Opt.Connections > 1 && TotalSize >= 512 * 1024)
        {
            var n = (int)Math.Min(Opt.Connections, Math.Max(1, TotalSize / (256 * 1024)));
            var size = TotalSize / n;
            long pos = 0;
            for (var i = 0; i < n; i++)
            {
                var end = i == n - 1 ? TotalSize - 1 : pos + size - 1;
                Chunks.Add(new DownloadChunk { Index = i, Start = pos, End = end });
                pos = end + 1;
            }
        }
        else
        {
            Chunks.Add(new DownloadChunk { Index = 0, Start = 0, End = TotalSize > 0 ? TotalSize - 1 : -1 });
        }
    }

    // ───────── one chunk ─────────

    private async Task RunChunkAsync(HttpClient http, DownloadChunk c, SafeFileHandle fh, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var ranged = AcceptRanges && TotalSize > 0;
            while (true)
            {
                if (c.Length >= 0 && Volatile.Read(ref c.Done) >= c.Length) { c.State = "done"; return; }
                try
                {
                    if (!ranged) Volatile.Write(ref c.Done, 0);
                    c.State = "downloading";

                    using var req = NewRequest(HttpMethod.Get, UrlToUse);
                    var from = c.Start + Volatile.Read(ref c.Done);
                    if (ranged) req.Headers.Range = new RangeHeaderValue(from, c.End >= 0 ? c.End : null);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (ranged && resp.StatusCode == HttpStatusCode.OK && from > 0)
                        throw new IOException("The server ignored the Range header.");
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var pos = from;
                    while (true)
                    {
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stall.CancelAfter(TimeSpan.FromSeconds(30));      // no data for 30 s → retry
                        var n = await stream.ReadAsync(buffer.AsMemory(), stall.Token);
                        if (n == 0) break;

                        long write = n;
                        if (c.End >= 0) write = Math.Min(write, c.Length - Volatile.Read(ref c.Done));
                        if (write <= 0) break;

                        await ThrottleAsync((int)write, ct);
                        await RandomAccess.WriteAsync(fh, buffer.AsMemory(0, (int)write), pos, ct);
                        pos += write;
                        Interlocked.Add(ref c.Done, write);
                        if (c.End >= 0 && Volatile.Read(ref c.Done) >= c.Length) break;
                    }

                    if (c.End >= 0 && Volatile.Read(ref c.Done) < c.Length)
                        throw new IOException("The connection closed before the chunk was complete.");

                    c.State = "done";
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    c.Retries++;
                    if (c.Retries > MaxRetries) { c.State = "failed"; throw; }
                    c.State = $"retry {c.Retries}/{MaxRetries}";
                    Note($"Chunk #{c.Index + 1}: {ex.Message} — retrying");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * c.Retries, 10)), ct);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        var limit = LimitBps;
        if (limit <= 0) return;
        var total = Interlocked.Add(ref _throttled, bytes);
        var ahead = total / (double)limit - _throttleClock.Elapsed.TotalSeconds;
        if (ahead > 0.005) await Task.Delay(TimeSpan.FromSeconds(Math.Min(ahead, 1.0)), ct);
    }

    // ───────── state / cleanup ─────────

    private void SaveState()
    {
        try
        {
            if (StatePath.Length == 0 || TotalSize <= 0 || !AcceptRanges || Chunks.Count == 0) return;
            var st = new ResumeState(Opt.Url, TotalSize, ETag, LastModified,
                Chunks.Select(c => new ChunkState(c.Start, c.End, Volatile.Read(ref c.Done))).ToList());
            File.WriteAllText(StatePath, JsonSerializer.Serialize(st));
        }
        catch { /* best effort */ }
    }

    private void CleanupFiles()
    {
        if (PartPath.Length > 0) TryDelete(PartPath);
        if (StatePath.Length > 0) TryDelete(StatePath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ───────── statistics (call from one thread only, e.g. the UI timer) ─────────

    public void Sample()
    {
        if (Phase != "Downloading") { Speed = 0; Eta = null; foreach (var c in Chunks) c.Speed = 0; return; }

        var t = _runClock.Elapsed.TotalSeconds;
        var done = Downloaded;

        _window.Enqueue((t, done));
        while (_window.Count > 2 && t - _window.Peek().T > 4) _window.Dequeue();
        var first = _window.Peek();
        var dt = t - first.T;
        if (dt > 0.25) Speed = Math.Max(0, (done - first.Bytes) / dt);

        AvgSpeed = t > 0.5 ? Math.Max(0, (done - _startBytes) / t) : 0;
        Eta = TotalSize > 0 && Speed > 1 ? TimeSpan.FromSeconds((TotalSize - done) / Speed) : null;

        foreach (var c in Chunks)
        {
            var d = Volatile.Read(ref c.Done);
            var span = t - c.LastT;
            if (span > 0.2)
            {
                var inst = Math.Max(0, (d - c.LastDone) / span);
                c.Speed = c.State == "downloading" ? c.Speed * 0.5 + inst * 0.5 : 0;
                c.LastDone = d;
                c.LastT = t;
            }
        }
    }
}
