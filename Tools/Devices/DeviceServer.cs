using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ASD;

/// <summary>A phone that is paired and connected right now.</summary>
internal sealed record PairedDevice(string Id, string Name, string Address, DateTime Since);

/// <summary>An IPv4 address of this PC that a phone on the same network could reach.</summary>
internal sealed record LocalAddress(string Interface, string Ip);

/// <summary>
/// ASD Connect: the server a phone (the ASD Connect app) pairs with.
///
/// • Listens on TCP 8443 with TLS (a self-signed certificate made at start; the phone checks its SHA-256 fingerprint, which is in the QR code).
/// • Speaks WebSocket on /ws. The first message must arrive within 5 seconds: {"type":"hello","token":"…","device":"…"}.
///   A valid token (random, valid 5 minutes, works once) is answered with {"type":"welcome"}; anything else with
///   {"type":"error","message":"Invalid token"} and the connection is closed.
/// • A paired device stays connected and is listed; Disconnect sends {"type":"bye"} and closes it.
///
/// It is built on TcpListener + SslStream + WebSocket.CreateFromStream (no ASP.NET / Kestrel framework to ship) and does nothing until
/// <see cref="Start"/> is called, which the Devices page does when the person presses "Pair a device".
/// </summary>
internal static class DeviceServer
{
    public const int Port = 8443;
    public const string FirewallRuleName = "ASD Connect";
    public static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(5);
    public const string FirewallCommand =
        "New-NetFirewallRule -DisplayName \"ASD Connect\" -Direction Inbound -Protocol TCP -LocalPort 8443 -Action Allow -Profile Private";

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxPending = 32;     // connections that have not finished the hello yet
    private const int MaxDevices = 8;

    private sealed class Conn
    {
        public WebSocket Ws = null!;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public string Address = "";
        public DateTime Since = DateTime.Now;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Conn> Clients = new();
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static X509Certificate2? _cert;
    private static string _token = "";
    private static DateTime _tokenExpires = DateTime.MinValue;
    private static int _pending;

    /// <summary>Raised (any thread) when the server starts or stops, or a device connects or leaves.</summary>
    public static event Action? Changed;

    public static bool Running { get { lock (Gate) return _listener != null; } }

    /// <summary>SHA-256 of the certificate, lowercase hex. It is part of the pairing link.</summary>
    public static string Fingerprint { get; private set; } = "";

    /// <summary>When the current pairing code stops working (UTC).</summary>
    public static DateTime PairingExpires { get { lock (Gate) return _tokenExpires; } }

    // ───────── start / stop ─────────

    /// <summary>Starts listening (nothing happens when it already does). Throws a SocketException when the port is used by another program.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_listener != null) return;
            _cert ??= MakeCertificate();
            Fingerprint = Convert.ToHexString(SHA256.HashData(_cert.RawData)).ToLowerInvariant();

            var listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            var cts = new CancellationTokenSource();
            _listener = listener;
            _cts = cts;
            var cert = _cert;
            _ = Task.Run(() => AcceptLoopAsync(listener, cert, cts.Token));
        }
        Changed?.Invoke();
    }

    /// <summary>Disconnects every device and closes the port.</summary>
    public static void Stop()
    {
        List<Conn> all;
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (Gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            _token = "";
            _tokenExpires = DateTime.MinValue;
            all = Clients.Values.ToList();
            Clients.Clear();
        }
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        foreach (var c in all) _ = Task.Run(() => ByeAsync(c));
        Changed?.Invoke();
    }

    /// <summary>A self-signed certificate. It is re-imported as a PFX: on Windows the key made by CreateSelfSigned is not usable for TLS otherwise ("No credentials are available").</summary>
    private static X509Certificate2 MakeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ASD", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    // ───────── pairing code ─────────

    /// <summary>A new one-time code (16 random bytes as hex), valid for <see cref="TokenLife"/>. The old one stops working.</summary>
    public static void RefreshToken()
    {
        lock (Gate)
        {
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _tokenExpires = DateTime.UtcNow + TokenLife;
        }
    }

    /// <summary>No pairing code is valid any more (the pairing window was closed).</summary>
    public static void ClearToken()
    {
        lock (Gate) { _token = ""; _tokenExpires = DateTime.MinValue; }
    }

    /// <summary>asdconnect://open?host=IP&amp;port=8443&amp;token=…&amp;fp=… (the text of the QR code).</summary>
    public static string BuildLink(string host)
    {
        lock (Gate) return $"asdconnect://open?host={host}&port={Port}&token={_token}&fp={Fingerprint}";
    }

    private static bool TakeToken(string? offered)
    {
        lock (Gate)
        {
            if (_token.Length == 0 || DateTime.UtcNow >= _tokenExpires || string.IsNullOrEmpty(offered)) return false;
            var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(_token));
            if (ok) { _token = ""; _tokenExpires = DateTime.MinValue; }   // works once
            return ok;
        }
    }

    // ───────── devices ─────────

    public static List<PairedDevice> Devices()
    {
        lock (Gate)
            return Clients.Values.OrderBy(c => c.Since).Select(c => new PairedDevice(c.Id, c.Name, c.Address, c.Since)).ToList();
    }

    /// <summary>Sends {"type":"bye"} and closes the connection.</summary>
    public static void Disconnect(string id)
    {
        Conn? conn;
        lock (Gate)
        {
            if (!Clients.Remove(id, out conn)) return;
        }
        Changed?.Invoke();
        _ = Task.Run(() => ByeAsync(conn));
    }

    private static async Task ByeAsync(Conn c)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync(c, "{\"type\":\"bye\"}", limit.Token);
            await c.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", limit.Token);
        }
        catch { /* the phone may already be gone */ }
        try { await Task.Delay(500); } catch { }   // so the phone can read it
        try { c.Ws.Abort(); } catch { }
    }

    // ───────── this PC's addresses ─────────

    /// <summary>IPv4 addresses of the networks this PC is on, the best first (a network with a gateway that is not a virtual adapter).</summary>
    public static List<LocalAddress> LocalAddresses()
    {
        var found = new List<(LocalAddress Address, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var name = (nic.Description + " " + nic.Name).ToLowerInvariant();
                bool virt = new[] { "virtual", "vmware", "vbox", "hyper-v", "vethernet", "wsl", "docker" }.Any(w => name.Contains(w));
                var rank = (gateway ? 0 : 2) + (virt ? 1 : 0);
                foreach (var u in props.UnicastAddresses)
                {
                    var ip = u.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    var text = ip.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // no DHCP answer
                    found.Add((new LocalAddress(nic.Name, text), rank));
                }
            }
        }
        catch { /* no network information */ }
        return found.OrderBy(f => f.Rank).Select(f => f.Address).DistinctBy(a => a.Ip).ToList();
    }

    // ───────── Windows Firewall ─────────

    /// <summary>Is there a rule named "ASD Connect"? (Reading needs no administrator permission.)</summary>
    public static bool FirewallRuleExists()
    {
        try
        {
            var start = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var process = Process.Start(start);
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return false; }
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Adds the inbound rule for TCP 8443 on Private networks. Windows asks for administrator permission; false when it was refused.</summary>
    public static async Task<bool> AddFirewallRuleAsync()
    {
        try
        {
            var start = new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={Port} profile=private")
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(start);
            if (process == null) return false;
            await process.WaitForExitAsync();
        }
        catch (Win32Exception) { return false; }   // the permission was not given
        return await Task.Run(FirewallRuleExists);
    }

    // ───────── connections ─────────

    private static async Task AcceptLoopAsync(TcpListener listener, X509Certificate2 cert, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(stop); }
            catch { break; }

            if (Interlocked.Increment(ref _pending) > MaxPending) { Interlocked.Decrement(ref _pending); tcp.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(tcp, cert, stop); }
                catch { /* a broken connection is not an error of ASD */ }
            });
        }
    }

    private static async Task HandleAsync(TcpClient tcp, X509Certificate2 cert, CancellationToken stop)
    {
        Conn? conn = null;
        bool counted = true;   // still counted in _pending (until the hello is answered)
        void Uncount() { if (counted) { counted = false; Interlocked.Decrement(ref _pending); } }
        try
        {
            using (tcp)
            {
                tcp.NoDelay = true;
                var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

                await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, handshake.Token);
                }

                if (!await UpgradeAsync(ssl, stop)) return;

                var ws = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
                conn = new Conn { Ws = ws, Address = address };

                // the first message must come within 5 seconds
                string? first;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(5));
                    first = await ReceiveTextAsync(ws, 4096, wait.Token);
                }
                if (first == null) return;

                string type = "", token = "", device = "";
                try
                {
                    using var doc = JsonDocument.Parse(first);
                    string S(string n) => doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    type = S("type"); token = S("token"); device = S("device");
                }
                catch (JsonException) { }

                bool okToken = type == "hello" && TakeToken(token);
                bool added = false;
                if (okToken)
                {
                    lock (Gate)
                    {
                        if (_listener != null && Clients.Count < MaxDevices)
                        {
                            conn.Name = CleanName(device);
                            Clients[conn.Id] = conn;
                            added = true;
                        }
                    }
                }

                if (!added)
                {
                    await SendAsync(conn, "{\"type\":\"error\",\"message\":\"Invalid token\"}", stop);
                    await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "Invalid token");
                    Notifier.Post(NoteKind.Warning, "Device refused", $"{address} used an invalid or expired pairing code.", "devices");
                    return;
                }

                await SendAsync(conn, "{\"type\":\"welcome\"}", stop);
                Uncount();
                Changed?.Invoke();
                Notifier.Post(NoteKind.Success, "Device connected", conn.Name, "devices");

                // stays open; the phone may say bye
                while (ws.State == WebSocketState.Open)
                {
                    var text = await ReceiveTextAsync(ws, 65536, stop);
                    if (text == null) break;
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "bye") break;
                    }
                    catch (JsonException) { }
                }
            }
        }
        finally
        {
            Uncount();
            if (conn != null)
            {
                bool wasListed;
                lock (Gate) wasListed = Clients.Remove(conn.Id);
                try { conn.Ws.Abort(); } catch { }
                if (wasListed)
                {
                    Changed?.Invoke();
                    Notifier.Post(NoteKind.Info, "Device disconnected", conn.Name, "devices");
                }
            }
        }
    }

    /// <summary>Reads the HTTP request and answers the WebSocket upgrade (only GET /ws).</summary>
    private static async Task<bool> UpgradeAsync(Stream stream, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(TimeSpan.FromSeconds(5));

        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length) { await ReplyAsync(stream, "431 Request Header Fields Too Large"); return false; }
            var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token);
            if (read <= 0) return false;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length < 2 || request[0] != "GET" || request[1].Split('?')[0] != "/ws")
        {
            await ReplyAsync(stream, "404 Not Found");
            return false;
        }

        string? key = null;
        bool upgrade = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) && value.Contains("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
        }
        if (!upgrade || string.IsNullOrEmpty(key))
        {
            await ReplyAsync(stream, "400 Bad Request");
            return false;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), limit.Token);
        await stream.FlushAsync(limit.Token);
        return true;
    }

    private static async Task ReplyAsync(Stream stream, string status)
    {
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"));
            await stream.FlushAsync();
        }
        catch { }
    }

    /// <summary>One text message, or null when the connection closed, sent something that is not text, or the message is longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType != WebSocketMessageType.Text) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > maxBytes) return null;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static async Task SendAsync(Conn c, string json, CancellationToken ct)
    {
        await c.SendLock.WaitAsync(ct);
        try
        {
            if (c.Ws.State == WebSocketState.Open)
                await c.Ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        }
        finally { c.SendLock.Release(); }
    }

    private static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseOutputAsync(status, description, limit.Token);
        }
        catch { }
    }

    private static string CleanName(string? name)
    {
        var text = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length > 60) text = text[..60];
        return text.Length == 0 ? "Phone" : text;
    }
}
