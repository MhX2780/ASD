using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ASD;

// ─────────────────────────────────────────────────────────────────────────────
//  Sending one file somewhere. Used by the Upload page and by the "upload" cell of Automation.
//    Dropbox  : through the Dropbox API (a refresh token from the one-time sign-in, kept in the Keys Store)
//    POST     : multipart form with the file in the field "file"
//    PUT      : the file is the request body
// ─────────────────────────────────────────────────────────────────────────────

internal enum UploadTarget { Dropbox, Post, Put }

internal sealed class UploadOptions
{
    public UploadTarget Target = UploadTarget.Dropbox;
    public string FilePath = "";
    /// <summary>Dropbox: a folder ending with "/" or the full file path ("" = the app folder's root). POST / PUT: the URL.</summary>
    public string Where = "";
    public List<(string Name, string Value)> Headers = new();
    public bool Insecure;                      // HTTP: accept any TLS certificate
    public bool Overwrite;                     // Dropbox: replace a file with the same name (otherwise it is renamed)
    public bool ShareLink = true;              // Dropbox: also create a share link
    public int TimeoutSeconds;                 // 0 = no limit
}

internal sealed class UploadResult
{
    public bool Ok;
    public int Status;                         // HTTP status, 0 = none
    public string Message = "";                // one line for the person
    public string Output = "";                 // the server's answer, or the Dropbox share link
    public long Bytes;
}

internal static class UploadEngine
{
    private const long SingleRequestLimit = 100L << 20;     // Dropbox: bigger files go through an upload session
    private const int ChunkSize = 16 << 20;

    private static readonly HttpClient Client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>"dropbox /Folder/", "post https://…" or "put https://…" → the target and what follows it.</summary>
    public static UploadTarget? ParseTarget(string text, out string where)
    {
        var t = (text ?? "").Trim();
        var i = t.IndexOfAny(new[] { ' ', '\t' });
        var word = (i < 0 ? t : t[..i]).ToLowerInvariant();
        where = i < 0 ? "" : t[(i + 1)..].Trim();
        return word switch { "dropbox" => UploadTarget.Dropbox, "post" => UploadTarget.Post, "put" => UploadTarget.Put, _ => null };
    }

    private static UploadResult Fail(string message, int status = 0, string output = "")
        => new() { Ok = false, Message = message, Status = status, Output = output };

    /// <summary>
    /// Sends the file. Cancelling <paramref name="ct"/> throws OperationCanceledException; every other problem comes back as a
    /// failed result (never an exception). <paramref name="progress"/> gets (bytes sent, total) from a background thread.
    /// </summary>
    public static async Task<UploadResult> RunAsync(UploadOptions o, string? dropboxAppKey, string? dropboxRefreshToken, Action<long, long>? progress, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(o.FilePath)) return Fail("No file was chosen.");
            var info = new FileInfo(o.FilePath);
            if (!info.Exists) return Fail($"The file does not exist: {o.FilePath}");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (o.TimeoutSeconds > 0) linked.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
            try
            {
                return o.Target == UploadTarget.Dropbox
                    ? await DropboxAsync(o, info, dropboxAppKey, dropboxRefreshToken, progress, linked.Token)
                    : await HttpAsync(o, info, progress, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail($"The upload took longer than {o.TimeoutSeconds} s and was stopped.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    // ───────── POST / PUT ─────────

    private static async Task<UploadResult> HttpAsync(UploadOptions o, FileInfo info, Action<long, long>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(o.Where, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Fail("The address must be a full URL starting with http:// or https://");

        var client = Client;
        HttpClient? own = null;
        if (o.Insecure)
        {
            own = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
                { Timeout = Timeout.InfiniteTimeSpan };
            client = own;
        }

        try
        {
            using var file = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            HttpContent body = new ProgressContent(file, info.Length, progress);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (o.Target == UploadTarget.Post)
            {
                var form = new MultipartFormDataContent();
                form.Add(body, "file", info.Name);
                body = form;
            }

            using var req = new HttpRequestMessage(o.Target == UploadTarget.Post ? HttpMethod.Post : HttpMethod.Put, uri) { Content = body };
            foreach (var (name, value) in o.Headers)
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && o.Target == UploadTarget.Post) continue;   // the form needs its own boundary
                if (!req.Headers.TryAddWithoutValidation(name, value)) body.Headers.TryAddWithoutValidation(name, value);
            }

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await ReadCappedAsync(resp, ct);
            var status = (int)resp.StatusCode;
            return new UploadResult
            {
                Ok = resp.IsSuccessStatusCode, Status = status, Bytes = info.Length, Output = text,
                Message = resp.IsSuccessStatusCode
                    ? $"Sent {SizeFmt.Bytes(info.Length)} (HTTP {status})"
                    : $"The server answered HTTP {status} {resp.ReasonPhrase}" + (text.Length > 0 ? ": " + Short(text) : ""),
            };
        }
        finally { own?.Dispose(); }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[64 * 1024];
        var n = 0;
        while (n < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) break;
            n += r;
        }
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static string Short(string text)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 300 ? t[..300] + "…" : t;
    }

    /// <summary>The file stream as a request body that reports how much was sent.</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _length;
        private readonly Action<long, long>? _progress;

        public ProgressContent(Stream source, long length, Action<long, long>? progress)
        {
            _source = source; _length = length; _progress = progress;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buf = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long sent = 0;
                int n;
                while ((n = await _source.ReadAsync(buf.AsMemory(0, 81920), cancellationToken)) > 0)
                {
                    await stream.WriteAsync(buf.AsMemory(0, n), cancellationToken);
                    sent += n;
                    _progress?.Invoke(sent, _length);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

    // ───────── Dropbox ─────────

    /// <summary>The JSON for the Dropbox-API-Arg header: HTTP headers are ASCII only, so every other character is written as \uXXXX.</summary>
    private static string ApiArg(Dictionary<string, object> arg)
    {
        var json = JsonSerializer.Serialize(arg);
        var sb = new StringBuilder(json.Length);
        foreach (var ch in json)
        {
            if (ch > 126) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string RemotePath(string where, string fileName)
    {
        var w = (where ?? "").Trim().Replace('\\', '/');
        if (w.Length == 0 || w == "/") return "/" + fileName;
        if (!w.StartsWith('/')) w = "/" + w;
        return w.EndsWith('/') ? w + fileName : w;
    }

    public static async Task<string> DropboxAccessTokenAsync(string appKey, string refreshToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", appKey),
            }),
        };
        using var resp = await Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Dropbox did not accept the saved sign-in (HTTP {(int)resp.StatusCode}): {Short(text)}. Sign in again from the Upload page (Dropbox menu).");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Dropbox sent no access token.");
    }

    private static async Task<UploadResult> DropboxAsync(UploadOptions o, FileInfo info, string? appKey, string? refreshToken, Action<long, long>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(refreshToken))
            return Fail("Dropbox is not signed in. Open the Upload page, use its Dropbox menu and choose Sign in.");

        var token = await DropboxAccessTokenAsync(appKey.Trim(), refreshToken.Trim(), ct);
        var path = RemotePath(o.Where, info.Name);
        var mode = o.Overwrite ? "overwrite" : "add";

        string finalPath;
        if (info.Length <= SingleRequestLimit)
        {
            using var file = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload") { Content = new ProgressContent(file, info.Length, progress) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", ApiArg(new() { ["path"] = path, ["mode"] = mode, ["autorename"] = true, ["mute"] = false }));
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await ReadCappedAsync(resp, ct);
            if (!resp.IsSuccessStatusCode) return Fail($"Dropbox answered HTTP {(int)resp.StatusCode}: {Short(text)}", (int)resp.StatusCode, text);
            finalPath = PathFromMetadata(text) ?? path;
        }
        else
        {
            finalPath = await DropboxSessionUploadAsync(token, info, path, mode, progress, ct);
        }

        var result = new UploadResult { Ok = true, Status = 200, Bytes = info.Length, Output = finalPath, Message = $"Sent {SizeFmt.Bytes(info.Length)} to Dropbox: {finalPath}" };
        if (o.ShareLink)
        {
            try
            {
                var link = await DropboxShareLinkAsync(token, finalPath, ct);
                if (link != null) { result.Output = link; result.Message += "  ·  share link created"; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Message += $"  ·  (no share link: {Short(ex.Message)})"; }
        }
        return result;
    }

    private static string? PathFromMetadata(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("path_display", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Files over 100 MB: start a session, append 16 MB pieces, finish with the last piece.</summary>
    private static async Task<string> DropboxSessionUploadAsync(string token, FileInfo info, string path, string mode, Action<long, long>? progress, CancellationToken ct)
    {
        await using var file = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buf = new byte[ChunkSize];
        string? session = null;
        long offset = 0;

        while (true)
        {
            var filled = 0;
            while (filled < buf.Length)
            {
                var r = await file.ReadAsync(buf.AsMemory(filled), ct);
                if (r == 0) break;
                filled += r;
            }
            var last = offset + filled >= info.Length;

            using var req = new HttpRequestMessage(HttpMethod.Post, session == null
                ? "https://content.dropboxapi.com/2/files/upload_session/start"
                : last ? "https://content.dropboxapi.com/2/files/upload_session/finish" : "https://content.dropboxapi.com/2/files/upload_session/append_v2");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (session == null)
                req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", ApiArg(new() { ["close"] = false }));
            else
            {
                var cursor = new Dictionary<string, object> { ["session_id"] = session, ["offset"] = offset };
                req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", last
                    ? ApiArg(new() { ["cursor"] = cursor, ["commit"] = new Dictionary<string, object> { ["path"] = path, ["mode"] = mode, ["autorename"] = true, ["mute"] = false } })
                    : ApiArg(new() { ["cursor"] = cursor, ["close"] = false }));
            }
            req.Content = new ByteArrayContent(buf, 0, filled);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await ReadCappedAsync(resp, ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Dropbox answered HTTP {(int)resp.StatusCode}: {Short(text)}");

            offset += filled;
            progress?.Invoke(offset, info.Length);

            if (session == null)
            {
                using var doc = JsonDocument.Parse(text);
                session = doc.RootElement.GetProperty("session_id").GetString();
                if (last)
                {
                    // the whole file fit in the first piece: finish with an empty piece
                    await DropboxFinishEmptyAsync(token, session!, offset, path, mode, ct);
                    return path;
                }
            }
            else if (last) return PathFromMetadata(text) ?? path;
        }
    }

    private static async Task DropboxFinishEmptyAsync(string token, string session, long offset, string path, string mode, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload_session/finish");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var cursor = new Dictionary<string, object> { ["session_id"] = session, ["offset"] = offset };
        req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", ApiArg(new()
            { ["cursor"] = cursor, ["commit"] = new Dictionary<string, object> { ["path"] = path, ["mode"] = mode, ["autorename"] = true, ["mute"] = false } }));
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await ReadCappedAsync(resp, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Dropbox answered HTTP {(int)resp.StatusCode}: {Short(text)}");
    }

    private static async Task<string?> DropboxShareLinkAsync(string token, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings")
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["path"] = path }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        if (resp.IsSuccessStatusCode) return doc.RootElement.GetProperty("url").GetString();

        // a link for this file already exists: Dropbox sends it back inside the error
        if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("shared_link_already_exists", out var x)
            && x.TryGetProperty("metadata", out var m) && m.TryGetProperty("url", out var u))
            return u.GetString();
        throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Short(text)}");
    }
}

/// <summary>The one-time Dropbox sign-in: the browser asks you to allow the app, ASD receives the answer on localhost (PKCE, no secret).</summary>
internal static class DropboxAuth
{
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The fixed local port Dropbox sends the answer to. Register  http://localhost:59989/  once under "Redirect URIs" in the Dropbox app.</summary>
    public const int RedirectPort = 59989;
    public static string RedirectUri => $"http://localhost:{RedirectPort}/";

    /// <summary>Returns the refresh token. Throws when the person says no, the wait runs out (5 minutes) or Dropbox refuses.</summary>
    public static async Task<string> SignInAsync(string appKey, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var redirect = RedirectUri;

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        try { listener.Start(); }
        catch (Exception ex) { throw new InvalidOperationException($"ASD could not listen on port {RedirectPort} (another program uses it?): {ex.Message}"); }

        var url = "https://www.dropbox.com/oauth2/authorize"
                  + "?client_id=" + Uri.EscapeDataString(appKey)
                  + "&response_type=code&token_access_type=offline"
                  + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                  + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                  + "&state=" + state;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var pending = listener.GetContextAsync();
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMinutes(5), ct));
        if (finished != pending) throw new TimeoutException("Nobody answered in the browser within 5 minutes.");
        var ctx = await pending;

        var q = ctx.Request.QueryString;
        var code = q["code"];
        var problem = q["error_description"] ?? q["error"];
        var ok = !string.IsNullOrEmpty(code) && q["state"] == state;
        var html = ok
            ? "<html><body style='font-family:Segoe UI;padding:40px'><h2>ASD is connected to Dropbox</h2><p>You can close this tab and go back to ASD.</p></body></html>"
            : "<html><body style='font-family:Segoe UI;padding:40px'><h2>Dropbox was not connected</h2><p>Go back to ASD and try again.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();

        if (!ok) throw new InvalidOperationException(problem != null ? $"Dropbox said: {problem}" : "The answer from Dropbox did not match this sign-in (try again).");

        using var http = new HttpClient();
        using var resp = await http.PostAsync("https://api.dropboxapi.com/oauth2/token", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("code", code!),
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("client_id", appKey),
            new KeyValuePair<string, string>("code_verifier", verifier),
            new KeyValuePair<string, string>("redirect_uri", redirect),
        }), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Dropbox refused the sign-in (HTTP {(int)resp.StatusCode}): {text}");
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("refresh_token", out var rt) || string.IsNullOrEmpty(rt.GetString()))
            throw new InvalidOperationException("Dropbox sent no refresh token (the app must be allowed to use \"offline\" access).");
        return rt.GetString()!;
    }
}

/// <summary>Where the Dropbox sign-in is kept: the app key in the settings, the refresh token as a key in the Keys Store.</summary>
internal static class DropboxStore
{
    public const string RefName = "Dropbox-Refresh-Token";

    /// <summary>The app key of ASD's own Dropbox app (a public value: the sign-in uses PKCE, there is no secret). Fill it in once after creating the app.</summary>
    public const string BuiltInAppKey = "";

    /// <summary>An app key the person typed to use their own Dropbox app instead; empty = use the built-in one.</summary>
    public static string CustomAppKey
    {
        get => SettingsStore.Get("dropbox.appkey", "");
        set => SettingsStore.Set("dropbox.appkey", value.Trim());
    }

    /// <summary>The key that is used: the person's own when they set one, otherwise ASD's.</summary>
    public static string AppKey => CustomAppKey.Length > 0 ? CustomAppKey : BuiltInAppKey;

    private static KeyEntry? Entry()
        => !KeyVault.Exists ? null : KeyVault.All().FirstOrDefault(k => string.Equals(KeyRefs.RefOf(k), RefName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a sign-in was saved (reading the entry list needs no unlock).</summary>
    public static bool SignedIn => AppKey.Length > 0 && Entry() != null;

    public static void SaveToken(string token)
    {
        var e = Entry() ?? new KeyEntry
        {
            Name = "Dropbox refresh token", Ref = RefName, Service = "Dropbox",
            Notes = "Written by ASD (Upload File). It lets ASD upload to your Dropbox app. Sign out from the Upload page to remove it.",
        };
        KeyVault.Upsert(e, token);
    }

    public static void Forget()
    {
        var e = Entry();
        if (e != null) KeyVault.Delete(e.Id);
    }
}
