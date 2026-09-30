using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ASD;

public sealed class JwtPage : ToolPage
{
    private readonly TextBox _token, _key, _header, _payload, _claims;
    private readonly CheckBox _b64Secret;
    private readonly TextBlock _verdict = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    public JwtPage() : base("jwt", "JWT Decoder", "Decode a token instantly. Verify HS/RS/PS/ES signatures or sign HS tokens. Everything stays on your machine.")
    {
        _token = Input("token", "Token", 110, "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9…");
        _header = Input("hdr", "Header (editable)", 150, persist: false);
        _payload = Input("pl", "Payload (editable)", 150, persist: false);
        _claims = Output("Claims", 130);
        _key = Input("key", "Secret (HS*) or public key / certificate in PEM format (RS*, PS*, ES*)", 100, persist: false);
        _b64Secret = Check("Secret is Base64 / Base64URL encoded");

        _token.TextChanged += (_, _) => Decode();

        Body.Children.Add(_token);
        Body.Children.Add(_verdict);
        Body.Children.Add(TwoCols(_header, _payload));
        Body.Children.Add(_claims);
        Body.Children.Add(_key);
        Body.Children.Add(_b64Secret);
        Body.Children.Add(Row(
            Btn("Verify signature", Verify, true),
            Btn("Sign HS256/384/512 from header + payload", Sign),
            CopyBtn("Copy token", () => _token.Text)));
    }

    protected override void OnClipboardInput(string text) => _token.Text = text;

    // ───────── helpers ─────────
    private static byte[] B64Url(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static string B64UrlEnc(byte[] b)
        => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Pretty(string json)
    {
        try { return JsonTools.Format(JsonTools.Parse(json)); } catch { return json; }
    }

    private string CleanToken()
    {
        var t = (_token.Text ?? "").Trim();
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t[7..].Trim();
        return t;
    }

    private void SetVerdict(string text, bool? ok)
    {
        _verdict.Text = text;
        _verdict.Foreground = ok switch
        {
            true => new SolidColorBrush(Colors.LimeGreen),
            false => new SolidColorBrush(Colors.OrangeRed),
            _ => new SolidColorBrush(Colors.Gray),
        };
    }

    // ───────── decode ─────────
    private void Decode()
    {
        var t = CleanToken();
        if (t.Length == 0)
        {
            _header.Text = _payload.Text = _claims.Text = "";
            SetVerdict("", null);
            return;
        }
        var parts = t.Split('.');
        if (parts.Length < 2) { SetVerdict("Not a JWT (expected header.payload.signature).", false); return; }
        try
        {
            var h = Encoding.UTF8.GetString(B64Url(parts[0]));
            var p = Encoding.UTF8.GetString(B64Url(parts[1]));
            _header.Text = Pretty(h);
            _payload.Text = Pretty(p);
            var (claims, timeStatus, timeOk) = DescribeClaims(p);
            _claims.Text = claims;
            var alg = (JsonTools.Parse(h) as JsonObject)?["alg"]?.ToString() ?? "?";
            SetVerdict($"Decoded ✔   alg: {alg}   {timeStatus}", timeOk);
        }
        catch (Exception ex)
        {
            SetVerdict("Cannot decode: " + ex.Message, false);
        }
    }

    private static (string text, string status, bool? ok) DescribeClaims(string payloadJson)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.UtcNow;
        string status = "";
        bool? ok = null;
        if (JsonTools.Parse(payloadJson) is not JsonObject o) return ("", "", null);

        foreach (var key in new[] { "iss", "sub", "aud", "jti", "scope", "azp" })
            if (o[key] is JsonNode v) sb.AppendLine($"{key,-10}{v.ToJsonString().Trim('"')}");

        foreach (var (key, label) in new[] { ("iat", "issued at"), ("nbf", "not before"), ("exp", "expires"), ("auth_time", "auth time") })
        {
            if (o[key] is not JsonValue jv || !jv.TryGetValue<double>(out var secs)) continue;
            var when = DateTimeOffset.FromUnixTimeSeconds((long)secs);
            sb.AppendLine($"{key,-10}{when.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC   ({label}: {TimeTools.Relative(when, now)})");
            if (key == "exp")
            {
                bool expired = when < now;
                status = expired ? $"⛔ expired {TimeTools.Relative(when, now)}" : $"⏳ {TimeTools.Relative(when, now).Replace("in ", "expires in ")}";
                ok = !expired;
            }
            if (key == "nbf" && when > now) { status = $"⚠ not valid yet ({TimeTools.Relative(when, now)})"; ok = false; }
        }
        return (sb.ToString().TrimEnd(), status, ok);
    }

    // ───────── verify ─────────
    private byte[] SecretBytes()
    {
        var s = _key.Text ?? "";
        return Is(_b64Secret) ? B64Url(s) : Encoding.UTF8.GetBytes(s);
    }

    private void Verify()
    {
        var t = CleanToken();
        var parts = t.Split('.');
        if (parts.Length != 3) { SetVerdict("Token must have three parts to verify a signature.", false); return; }
        try
        {
            var header = JsonTools.Parse(Encoding.UTF8.GetString(B64Url(parts[0]))) as JsonObject;
            var alg = header?["alg"]?.GetValue<string>() ?? "";
            var data = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var sig = parts[2].Length == 0 ? Array.Empty<byte>() : B64Url(parts[2]);
            var pem = _key.Text ?? "";
            var hn = alg.EndsWith("384") ? HashAlgorithmName.SHA384 : alg.EndsWith("512") ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
            bool ok;

            if (alg.StartsWith("HS"))
            {
                if (pem.Length == 0) { SetVerdict("Enter the secret first.", false); return; }
                ok = CryptographicOperations.FixedTimeEquals(HmacFor(alg, SecretBytes(), data), sig);
            }
            else if (alg.StartsWith("RS") || alg.StartsWith("PS"))
            {
                using var rsa = LoadRsa(pem);
                ok = rsa.VerifyData(data, sig, hn, alg.StartsWith("RS") ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);
            }
            else if (alg is "ES256" or "ES384" or "ES512")
            {
                using var ec = LoadEc(pem);
                ok = ec.VerifyData(data, sig, hn, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            else if (alg.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                SetVerdict("alg is 'none': this token is unsigned and must not be trusted.", false);
                return;
            }
            else { SetVerdict($"Algorithm '{alg}' is not supported for verification.", false); return; }

            SetVerdict(ok ? $"Signature valid ✔ ({alg})" : $"Signature INVALID ✘ ({alg})", ok);
        }
        catch (Exception ex)
        {
            SetVerdict("Verification failed: " + ex.Message, false);
        }
    }

    private static byte[] HmacFor(string alg, byte[] key, byte[] data) => alg switch
    {
        "HS256" => HMACSHA256.HashData(key, data),
        "HS384" => HMACSHA384.HashData(key, data),
        "HS512" => HMACSHA512.HashData(key, data),
        _ => throw new NotSupportedException($"Algorithm '{alg}' is not supported."),
    };

    private static RSA LoadRsa(string pem)
    {
        if (pem.Contains("CERTIFICATE"))
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            return cert.GetRSAPublicKey() ?? throw new InvalidOperationException("Certificate has no RSA public key.");
        }
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    private static ECDsa LoadEc(string pem)
    {
        if (pem.Contains("CERTIFICATE"))
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            return cert.GetECDsaPublicKey() ?? throw new InvalidOperationException("Certificate has no ECDSA public key.");
        }
        var ec = ECDsa.Create();
        ec.ImportFromPem(pem);
        return ec;
    }

    // ───────── sign ─────────
    private void Sign()
    {
        var hdr = JsonTools.Parse(_header.Text ?? "") as JsonObject ?? throw new FormatException("Header must be a JSON object.");
        var payload = JsonTools.Parse(_payload.Text ?? "") ?? throw new FormatException("Payload is empty.");
        var alg = hdr["alg"]?.GetValue<string>() ?? "HS256";
        if (hdr["alg"] == null) hdr["alg"] = alg;
        if (hdr["typ"] == null) hdr["typ"] = "JWT";
        if ((_key.Text ?? "").Length == 0) throw new InvalidOperationException("Enter a secret to sign with.");

        var h = B64UrlEnc(Encoding.UTF8.GetBytes(JsonTools.Minify(hdr)));
        var p = B64UrlEnc(Encoding.UTF8.GetBytes(JsonTools.Minify(payload)));
        var sig = HmacFor(alg, SecretBytes(), Encoding.ASCII.GetBytes(h + "." + p));
        _token.Text = $"{h}.{p}.{B64UrlEnc(sig)}";
        Status.Text = $"Signed with {alg} ✔";
    }
}
