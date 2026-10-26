using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ASD;

public sealed class HttpTrackOptions
{
    public string Method = "GET";
    public string Url = "";
    public List<(string Name, string Value)> Headers = new();
    public string? Body;
    public bool Follow = true;
    public int MaxRedirects = 10;
    public bool Insecure;
    public bool Decompress = true;
    public bool PrettyJson = true;
    public string Version = "auto";   // "auto" | "1.1" | "2"
    public int TimeoutSec = 60;
    public int BodyPreviewKb = 256;
}

/// <summary>Bytes / timings seen on the (already decrypted) connection for one request.</summary>
internal sealed class WireStats
{
    public long Out, In;
    public double FirstOut = -1, LastOut = -1, FirstIn = -1, LastIn = -1;
    public readonly MemoryStream RawOut = new();
}

/// <summary>Transparent stream wrapper that records what the HTTP stack writes and reads.</summary>
internal sealed class TapStream : Stream
{
    private readonly Stream _inner;
    private readonly Func<WireStats> _stats;
    private readonly Stopwatch _clock;
    private readonly Action<WireStats> _onFirstIn;

    public TapStream(Stream inner, Func<WireStats> stats, Stopwatch clock, Action<WireStats> onFirstIn)
    {
        _inner = inner; _stats = stats; _clock = clock; _onFirstIn = onFirstIn;
    }

    private void Out(ReadOnlySpan<byte> data)
    {
        var w = _stats();
        var t = _clock.Elapsed.TotalMilliseconds;
        lock (w)
        {
            if (w.FirstOut < 0) w.FirstOut = t;
            w.LastOut = t;
            w.Out += data.Length;
            var room = 16384 - (int)w.RawOut.Length;
            if (room > 0) w.RawOut.Write(data[..Math.Min(room, data.Length)]);
        }
    }

    private void In(int n)
    {
        if (n <= 0) return;
        var w = _stats();
        var t = _clock.Elapsed.TotalMilliseconds;
        bool first = false;
        lock (w)
        {
            if (w.FirstIn < 0) { w.FirstIn = t; first = true; }
            w.LastIn = t;
            w.In += n;
        }
        if (first) _onFirstIn(w);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count); In(n); return n;
    }
    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer); In(n); return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        In(n);
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Out(buffer.AsSpan(offset, count)); _inner.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Out(buffer); _inner.Write(buffer);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Out(buffer.Span);
        await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}

public static class HttpTracker
{
    private static readonly JsonSerializerOptions PrettyOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Sends one request and reports every step as log lines: DNS, TCP, TLS, the request as it goes
    /// over the wire, the server's answer (status, headers, body) and a timing breakdown.
    /// Redirects are followed manually so each hop gets its own full log.
    /// </summary>
    public static async Task RunAsync(HttpTrackOptions o, Action<string> emit, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var gate = new object();

        void Log(string tag, string msg)
        {
            lock (gate)
            {
                var t = clock.Elapsed.TotalMilliseconds;
                var first = true;
                foreach (var line in msg.Replace("\r\n", "\n").Split('\n'))
                {
                    emit(first ? $"[{t,8:0.0} ms] {tag,-6} {line}" : new string(' ', 21) + line);
                    first = false;
                }
            }
        }

        // ── per-connection / per-request state (captured by the callbacks below) ──
        var wire = new WireStats();
        string negotiated = "";
        double dnsMs = -1, tcpMs = -1, tlsMs = -1, tcpEndedAt = -1;
        int newConnections = 0;

        var url = o.Url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Log("ERROR", "Enter a valid http:// or https:// URL.");
            return;
        }

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = o.Decompress ? DecompressionMethods.All : DecompressionMethods.None,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSec, 5, 60)),
        };

        handler.ConnectCallback = async (ctx, token) =>
        {
            newConnections++;
            var host = ctx.DnsEndPoint.Host;
            var port = ctx.DnsEndPoint.Port;

            var sw = Stopwatch.StartNew();
            Log("DNS", $"Resolving {host} …");
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal)) addresses = new[] { literal };
            else addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            dnsMs = sw.Elapsed.TotalMilliseconds;
            Log("DNS", $"{host} → {string.Join(", ", addresses.Select(a => a.ToString()))}   ({dnsMs:0.0} ms)");

            Exception? last = null;
            foreach (var addr in addresses)
            {
                var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                sw.Restart();
                try
                {
                    Log("TCP", $"Connecting to {addr}:{port} …");
                    await socket.ConnectAsync(new IPEndPoint(addr, port), token).ConfigureAwait(false);
                    tcpMs = sw.Elapsed.TotalMilliseconds;
                    tcpEndedAt = clock.Elapsed.TotalMilliseconds;
                    Log("TCP", $"Connected  {socket.LocalEndPoint} → {socket.RemoteEndPoint}   ({tcpMs:0.0} ms)");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    socket.Dispose();
                    last = ex;
                    Log("TCP", $"Failed to connect to {addr}: {ex.Message}");
                }
            }
            throw last ?? new SocketException((int)SocketError.HostNotFound);
        };

        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                try
                {
                    var sb = new StringBuilder();
                    if (sender is SslStream ss)
                    {
                        try { if (ss.SslProtocol != System.Security.Authentication.SslProtocols.None) sb.AppendLine($"Protocol    {ss.SslProtocol}"); } catch { }
                    }
                    if (cert != null)
                    {
                        var c2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                        sb.AppendLine($"Subject     {c2.Subject}");
                        sb.AppendLine($"Issuer      {c2.Issuer}");
                        sb.AppendLine($"Valid       {c2.NotBefore:yyyy-MM-dd} → {c2.NotAfter:yyyy-MM-dd}");
                        sb.AppendLine($"Thumbprint  {c2.Thumbprint}");
                        if (chain != null) sb.AppendLine($"Chain       {chain.ChainElements.Count} certificates");
                    }
                    sb.Append(errors == SslPolicyErrors.None
                        ? "Certificate is valid ✔"
                        : $"Certificate problems: {errors}" + (o.Insecure ? "  (ignored)" : "  ✘"));
                    Log("TLS", "Server certificate received\n" + sb);
                }
                catch { /* logging only */ }
                return o.Insecure || errors == SslPolicyErrors.None;
            },
        };

        void OnFirstIn(WireStats w)
        {
            string head;
            long sent;
            byte[] raw;
            lock (w) { sent = w.Out; raw = w.RawOut.ToArray(); }

            if (negotiated.StartsWith("1."))
            {
                head = RenderRawHead(raw, sent);
                Log("WIRE", $"➜ Request on the wire: {sent:N0} bytes  (written between {w.FirstOut:0.0} and {w.LastOut:0.0} ms)\n{head}");
            }
            else
            {
                Log("WIRE", $"➜ Request on the wire: {sent:N0} bytes  (HTTP/{negotiated} binary frames, headers HPACK-compressed)");
            }
            Log("WIRE", $"⬅ First response byte arrived {Math.Max(0, w.FirstIn - w.LastOut):0.0} ms after the request was sent");
        }

        handler.PlaintextStreamFilter = (ctx, token) =>
        {
            negotiated = ctx.NegotiatedHttpVersion.ToString();
            var https = ctx.InitialRequestMessage.RequestUri?.Scheme == "https";
            if (https && tcpEndedAt >= 0)
            {
                tlsMs = clock.Elapsed.TotalMilliseconds - tcpEndedAt;
                Log("TLS", $"Handshake finished   ({tlsMs:0.0} ms)   ·   negotiated HTTP/{negotiated}");
            }
            else
            {
                Log("CONN", $"Connection ready   ·   HTTP/{negotiated}");
            }
            Stream tap = new TapStream(ctx.PlaintextStream, () => wire, clock, OnFirstIn);
            return new ValueTask<Stream>(tap);
        };

        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSec)));

        var method = o.Method.Trim().ToUpperInvariant();
        var body = string.IsNullOrEmpty(o.Body) ? null : o.Body;
        var headers = new List<(string Name, string Value)>(o.Headers);
        var hop = 0;

        try
        {
            while (true)
            {
                wire = new WireStats();
                negotiated = "";
                newConnections = 0;
                dnsMs = tcpMs = tlsMs = -1;

                using var req = BuildRequest(method, uri, headers, body, o.Version);
                Log("START", $"{(hop == 0 ? "Request" : $"Redirect #{hop}")}: {method} {uri}");
                Log("REQ", Describe(req, body));

                var tStart = clock.Elapsed.TotalMilliseconds;
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                var tHdr = clock.Elapsed.TotalMilliseconds;

                var status = (int)resp.StatusCode;
                var sbh = new StringBuilder();
                sbh.AppendLine($"HTTP/{resp.Version} {status} {resp.ReasonPhrase}    (headers complete after {tHdr - tStart:0.0} ms)");
                foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                    foreach (var v in h.Value) sbh.AppendLine($"{h.Key}: {v}");
                Log("RES", sbh.ToString().TrimEnd());

                // ── body ──
                var cap = Math.Max(1, o.BodyPreviewKb) * 1024;
                var preview = new MemoryStream();
                long total = 0;
                var lastProgress = clock.Elapsed.TotalMilliseconds;
                using (var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await stream.ReadAsync(buf, linked.Token).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (preview.Length < cap) preview.Write(buf, 0, (int)Math.Min(n, cap - preview.Length));
                        var now = clock.Elapsed.TotalMilliseconds;
                        if (now - lastProgress > 1000)
                        {
                            lastProgress = now;
                            var sec = Math.Max(0.001, (now - tHdr) / 1000.0);
                            Log("BODY", $"… {SizeFmt.Bytes(total)} received so far  ({SizeFmt.Speed(total / sec)})");
                        }
                    }
                }
                var tEnd = clock.Elapsed.TotalMilliseconds;

                long wireIn;
                lock (wire) wireIn = wire.In;
                var dlSec = Math.Max(0.001, (tEnd - tHdr) / 1000.0);
                Log("BODY", $"{total:N0} bytes ({SizeFmt.Bytes(total)}) downloaded in {tEnd - tHdr:0.0} ms" +
                            (total > 0 ? $"   ({SizeFmt.Speed(total / dlSec)})" : "") +
                            $"\nOn the wire: {wireIn:N0} bytes received (headers + body" +
                            (o.Decompress ? ", before decompression" : "") + ")");

                var isRedirect = status is 301 or 302 or 303 or 307 or 308;
                if (!isRedirect && total > 0)
                    Log("DATA", DecodePreview(preview.ToArray(), resp.Content.Headers, o.PrettyJson, total > preview.Length));

                Timing(Log, tStart, tHdr, tEnd, wire, dnsMs, tcpMs, tlsMs, newConnections);

                // ── redirects ──
                if (isRedirect && resp.Headers.Location != null)
                {
                    if (!o.Follow)
                    {
                        Log("REDIR", $"Server redirects to {resp.Headers.Location}  (redirect following is off)");
                        break;
                    }
                    if (++hop > o.MaxRedirects)
                    {
                        Log("ERROR", $"Stopped: more than {o.MaxRedirects} redirects.");
                        break;
                    }
                    var next = new Uri(uri, resp.Headers.Location);
                    Log("REDIR", $"{status} → {next}");

                    if (!string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        var removed = headers.RemoveAll(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                                                          || h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                                                          || h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
                        if (removed > 0) Log("REDIR", "Different host: Authorization / Cookie / Host headers were dropped.");
                    }
                    if (status == 303 || ((status == 301 || status == 302) && method == "POST"))
                    {
                        if (method != "GET" && method != "HEAD") { method = "GET"; body = null; Log("REDIR", "Method changed to GET, body dropped."); }
                    }
                    uri = next;
                    continue;
                }
                break;
            }

            Log("DONE", $"Finished in {clock.Elapsed.TotalMilliseconds:0.0} ms");
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) Log("STOP", "Cancelled.");
            else Log("ERROR", $"Timed out after {o.TimeoutSec} s.");
        }
        catch (Exception ex)
        {
            Log("ERROR", ExceptionChain(ex));
        }
    }

    // ───────── request building / description ─────────

    private static HttpRequestMessage BuildRequest(string method, Uri uri, List<(string Name, string Value)> headers, string? body, string version)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), uri);
        switch (version)
        {
            case "1.1": req.Version = HttpVersion.Version11; req.VersionPolicy = HttpVersionPolicy.RequestVersionExact; break;
            case "2": req.Version = HttpVersion.Version20; req.VersionPolicy = HttpVersionPolicy.RequestVersionExact; break;
            default: req.Version = HttpVersion.Version20; req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower; break;
        }

        HttpContent? content = null;
        if (body != null)
        {
            content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = null;
        }

        foreach (var (name, value) in headers)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) { req.Headers.Host = value; continue; }
            if (!req.Headers.TryAddWithoutValidation(name, value))
                content?.Headers.TryAddWithoutValidation(name, value);
        }

        if (content != null)
        {
            if (content.Headers.ContentType == null)
                content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=utf-8");
            req.Content = content;
        }
        return req;
    }

    private static string Describe(HttpRequestMessage req, string? body)
    {
        var sb = new StringBuilder();
        var policy = req.VersionPolicy switch
        {
            HttpVersionPolicy.RequestVersionExact => $"HTTP/{req.Version} only",
            _ => "HTTP/2 if the server supports it, otherwise HTTP/1.1",
        };
        sb.AppendLine($"{req.Method} {req.RequestUri!.PathAndQuery}   ({policy})");
        if (req.Headers.Host != null) sb.AppendLine($"Host: {req.Headers.Host}");
        foreach (var h in req.Headers) foreach (var v in h.Value) sb.AppendLine($"{h.Key}: {v}");
        if (req.Content != null)
            foreach (var h in req.Content.Headers) foreach (var v in h.Value) sb.AppendLine($"{h.Key}: {v}");
        sb.Append("(the HTTP stack adds a few more headers such as Host / Accept-Encoding — see the wire view below)");
        if (body != null)
        {
            var shown = body.Length > 2000 ? body[..2000] + " …" : body;
            sb.Append($"\n\nBody ({Encoding.UTF8.GetByteCount(body):N0} bytes):\n{shown}");
        }
        return sb.ToString();
    }

    private static string RenderRawHead(byte[] raw, long totalSent)
    {
        var text = Encoding.Latin1.GetString(raw);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = end >= 0 ? text[..end] : text;
        var sb = new StringBuilder();
        foreach (var line in head.Split("\r\n"))
            sb.AppendLine("│ " + new string(line.Select(c => c < 32 || c > 126 ? '·' : c).ToArray()));
        if (end >= 0)
        {
            var bodyBytes = Math.Max(0, totalSent - (end + 4));
            sb.Append(bodyBytes > 0 ? $"└─ + {bodyBytes:N0} body bytes" : "└─ (no body)");
        }
        return sb.ToString().TrimEnd();
    }

    // ───────── response helpers ─────────

    private static string DecodePreview(byte[] bytes, HttpContentHeaders headers, bool pretty, bool truncated)
    {
        var mt = headers.ContentType?.MediaType ?? "";
        var textual = mt.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                      || mt.Contains("json") || mt.Contains("xml") || mt.Contains("javascript")
                      || mt.Contains("html") || mt.Contains("x-www-form-urlencoded");
        if (mt.Length == 0)
            textual = bytes.Take(512).All(b => b >= 32 || b is 9 or 10 or 13);

        var note = truncated ? $"\n… preview cut at {bytes.Length:N0} bytes" : "";
        if (!textual) return "Binary content (" + (string.IsNullOrEmpty(mt) ? "unknown type" : mt) + ") — first bytes:\n" + HexDump(bytes, 192) + note;

        var enc = Encoding.UTF8;
        try { if (!string.IsNullOrWhiteSpace(headers.ContentType?.CharSet)) enc = Encoding.GetEncoding(headers.ContentType!.CharSet!.Trim('"')); } catch { }
        var text = enc.GetString(bytes);

        if (pretty && mt.Contains("json") && !truncated)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                text = JsonSerializer.Serialize(doc.RootElement, PrettyOpts);
            }
            catch { /* not valid JSON: show as-is */ }
        }
        return text + note;
    }

    private static string HexDump(byte[] data, int max)
    {
        var sb = new StringBuilder();
        var len = Math.Min(max, data.Length);
        for (var i = 0; i < len; i += 16)
        {
            var chunk = data.Skip(i).Take(Math.Min(16, len - i)).ToArray();
            sb.Append($"{i:X8}  ");
            sb.Append(string.Join(" ", chunk.Select(b => b.ToString("X2"))).PadRight(47));
            sb.Append("  ");
            sb.AppendLine(new string(chunk.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray()));
        }
        return sb.ToString().TrimEnd();
    }

    private static void Timing(Action<string, string> log, double tStart, double tHdr, double tEnd,
                               WireStats w, double dns, double tcp, double tls, int newConns)
    {
        double wait = w.FirstIn >= 0 && w.LastOut >= 0 ? Math.Max(0, w.FirstIn - w.LastOut) : Math.Max(0, tHdr - tStart);
        var download = Math.Max(0, tEnd - tHdr);
        var total = Math.Max(0.001, tEnd - tStart);

        var rows = new List<(string Name, double Ms)>();
        if (newConns > 0)
        {
            if (dns >= 0) rows.Add(("DNS lookup", dns));
            if (tcp >= 0) rows.Add(("TCP connect", tcp));
            if (tls >= 0) rows.Add(("TLS handshake", tls));
        }
        rows.Add(("Server wait (TTFB)", wait));
        rows.Add(("Content download", download));

        var sb = new StringBuilder();
        if (newConns == 0) sb.AppendLine("Connection reused from the pool (keep-alive): no DNS / TCP / TLS.");
        foreach (var (name, ms) in rows)
        {
            var bar = new string('█', Math.Clamp((int)Math.Round(ms / total * 30), ms > 0 ? 1 : 0, 30));
            sb.AppendLine($"{name,-20} {ms,9:0.0} ms  {bar}");
        }
        sb.Append($"{"Total (this request)",-20} {total,9:0.0} ms");
        log("TIME", sb.ToString());
    }

    private static string ExceptionChain(Exception ex)
    {
        var sb = new StringBuilder();
        var depth = 0;
        for (var e = ex; e != null && depth < 6; e = e.InnerException, depth++)
            sb.AppendLine($"{new string(' ', depth * 2)}{e.GetType().Name}: {e.Message}");
        return sb.ToString().TrimEnd();
    }
}

public static class SizeFmt
{
    public static string Bytes(long n) => Bytes((double)n);

    public static string Bytes(double n)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return i == 0 ? $"{n:0} B" : n < 10 ? $"{n:0.00} {u[i]}" : n < 100 ? $"{n:0.0} {u[i]}" : $"{n:0} {u[i]}";
    }

    public static string Speed(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
