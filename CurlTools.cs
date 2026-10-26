using System.Text.RegularExpressions;

namespace ASD;

public sealed class CurlRequest
{
    public string Url = "";
    public string Method = "GET";
    public List<(string Name, string Value)> Headers = new();
    public string? Body;
    public string? User;
    public bool Insecure, Follow;
    public List<string> Notes = new();

    public string? Header(string name)
        => Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool HasHeader(string name) => Headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public static class CurlTools
{
    private static readonly HashSet<char> ShortWithArg = new("XHduAbeomFTwxEcDKrUyYztC");

    public static List<string> Tokenize(string input)
    {
        var s = Regex.Replace(input, @"\\\r?\n|\^\r?\n|`\r?\n", " ");
        var args = new List<string>();
        var sb = new StringBuilder();
        bool inTok = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (inTok) { args.Add(sb.ToString()); sb.Clear(); inTok = false; }
                continue;
            }
            inTok = true;
            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'') sb.Append(s[i++]);
            }
            else if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\' || s[i + 1] == '$' || s[i + 1] == '`')) i++;
                    sb.Append(s[i]);
                    i++;
                }
            }
            else if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
            else sb.Append(c);
        }
        if (inTok) args.Add(sb.ToString());
        return args;
    }

    public static CurlRequest Parse(string command)
    {
        var args = Tokenize(command.Trim());
        if (args.Count > 0 && args[0].StartsWith("curl", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
        if (args.Count == 0) throw new FormatException("Paste a cURL command (it should start with 'curl').");

        var r = new CurlRequest();
        string? method = null;
        bool head = false;

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string NextArg() => i + 1 < args.Count ? args[++i] : "";

            if (a.StartsWith("--"))
            {
                string name = a, val = "";
                bool inline = false;
                var eq = a.IndexOf('=');
                if (eq > 0) { name = a[..eq]; val = a[(eq + 1)..]; inline = true; }
                switch (name)
                {
                    case "--request": method = inline ? val : NextArg(); break;
                    case "--header": AddHeader(r, inline ? val : NextArg()); break;
                    case "--data": case "--data-raw": case "--data-binary": case "--data-ascii": case "--data-urlencode":
                        AddBody(r, inline ? val : NextArg()); break;
                    case "--json":
                        AddBody(r, inline ? val : NextArg());
                        if (!r.HasHeader("Content-Type")) r.Headers.Add(("Content-Type", "application/json"));
                        if (!r.HasHeader("Accept")) r.Headers.Add(("Accept", "application/json"));
                        break;
                    case "--user": r.User = inline ? val : NextArg(); break;
                    case "--user-agent": r.Headers.Add(("User-Agent", inline ? val : NextArg())); break;
                    case "--cookie": r.Headers.Add(("Cookie", inline ? val : NextArg())); break;
                    case "--referer": r.Headers.Add(("Referer", inline ? val : NextArg())); break;
                    case "--insecure": r.Insecure = true; break;
                    case "--location": r.Follow = true; break;
                    case "--head": head = true; break;
                    case "--url": r.Url = inline ? val : NextArg(); break;
                    case "--form": r.Notes.Add("multipart form (-F " + (inline ? val : NextArg()) + ") is not converted."); break;
                    case "--output": case "--max-time": case "--proxy": case "--cert": case "--key": case "--cacert":
                    case "--connect-timeout": case "--retry": case "--resolve": case "--cookie-jar": case "--dump-header":
                    case "--config": case "--range": case "--write-out": case "--upload-file": case "--limit-rate":
                        if (!inline) NextArg();
                        break;
                    default: break;   // flags without argument (--compressed, --silent, ...)
                }
            }
            else if (a.StartsWith("-") && a.Length > 1)
            {
                for (int k = 1; k < a.Length; k++)
                {
                    char ch = a[k];
                    if (ShortWithArg.Contains(ch))
                    {
                        string val = k + 1 < a.Length ? a[(k + 1)..] : NextArg();
                        switch (ch)
                        {
                            case 'X': method = val; break;
                            case 'H': AddHeader(r, val); break;
                            case 'd': AddBody(r, val); break;
                            case 'u': r.User = val; break;
                            case 'A': r.Headers.Add(("User-Agent", val)); break;
                            case 'b': r.Headers.Add(("Cookie", val)); break;
                            case 'e': r.Headers.Add(("Referer", val)); break;
                            case 'F': r.Notes.Add("multipart form (-F " + val + ") is not converted."); break;
                        }
                        break;   // the rest of the token was the value
                    }
                    switch (ch)
                    {
                        case 'k': r.Insecure = true; break;
                        case 'L': r.Follow = true; break;
                        case 'I': head = true; break;
                    }
                }
            }
            else if (r.Url.Length == 0) r.Url = a;
        }

        if (r.Url.Length == 0) throw new FormatException("No URL found in the cURL command.");
        r.Method = (method ?? (head ? "HEAD" : r.Body != null ? "POST" : "GET")).ToUpperInvariant();
        if (r.Body != null && r.Body.StartsWith("@")) r.Notes.Add($"the body reads from a file ({r.Body}); adapt the code to load it.");
        return r;
    }

    private static void AddHeader(CurlRequest r, string h)
    {
        var i = h.IndexOf(':');
        if (i > 0) r.Headers.Add((h[..i].Trim(), h[(i + 1)..].Trim()));
    }

    private static void AddBody(CurlRequest r, string data)
        => r.Body = r.Body == null ? data : r.Body + "&" + data;

    // ───────────── code generators ─────────────
    private static string J(string s) => JsonTools.Quote(s);

    private static string Cs(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    private static bool IsContentHeader(string n)
        => n.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    private static string Notes(CurlRequest r, string comment)
        => string.Concat(r.Notes.Select(n => $"{comment} Note: {n}\n"));

    public static string ToCSharp(CurlRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(Notes(r, "//"));
        var handlerProps = new List<string>();
        if (!r.Follow) handlerProps.Add("AllowAutoRedirect = false");
        if (r.Insecure) handlerProps.Add("ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator");
        if (handlerProps.Count > 0)
        {
            sb.AppendLine($"var handler = new HttpClientHandler {{ {string.Join(", ", handlerProps)} }};");
            sb.AppendLine("using var client = new HttpClient(handler);");
        }
        else sb.AppendLine("using var client = new HttpClient();");

        var method = r.Method switch
        {
            "GET" => "HttpMethod.Get", "POST" => "HttpMethod.Post", "PUT" => "HttpMethod.Put", "DELETE" => "HttpMethod.Delete",
            "PATCH" => "HttpMethod.Patch", "HEAD" => "HttpMethod.Head", "OPTIONS" => "HttpMethod.Options",
            _ => $"new HttpMethod({Cs(r.Method)})",
        };
        sb.AppendLine($"var request = new HttpRequestMessage({method}, {Cs(r.Url)});");
        foreach (var (n, v) in r.Headers.Where(h => !IsContentHeader(h.Name)))
            sb.AppendLine($"request.Headers.TryAddWithoutValidation({Cs(n)}, {Cs(v)});");
        if (r.User != null)
            sb.AppendLine($"request.Headers.Authorization = new AuthenticationHeaderValue(\"Basic\", Convert.ToBase64String(Encoding.UTF8.GetBytes({Cs(r.User)})));");
        if (r.Body != null)
        {
            var ct = (r.Header("Content-Type") ?? "application/x-www-form-urlencoded").Split(';')[0].Trim();
            sb.AppendLine($"request.Content = new StringContent({Cs(r.Body)}, Encoding.UTF8, {Cs(ct)});");
        }
        sb.AppendLine();
        sb.AppendLine("var response = await client.SendAsync(request);");
        sb.AppendLine("Console.WriteLine((int)response.StatusCode);");
        sb.AppendLine("Console.WriteLine(await response.Content.ReadAsStringAsync());");
        return sb.ToString().TrimEnd();
    }

    public static string ToJavaScript(CurlRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(Notes(r, "//"));
        if (r.Insecure) sb.AppendLine("// -k: disable TLS verification in your runtime (e.g. NODE_TLS_REJECT_UNAUTHORIZED=0)");
        sb.AppendLine($"const response = await fetch({J(r.Url)}, {{");
        sb.AppendLine($"  method: {J(r.Method)},");
        var headers = r.Headers.Select(h => $"    {J(h.Name)}: {J(h.Value)}").ToList();
        if (r.User != null) headers.Add($"    \"Authorization\": \"Basic \" + btoa({J(r.User)})");
        if (headers.Count > 0)
        {
            sb.AppendLine("  headers: {");
            sb.AppendLine(string.Join(",\n", headers));
            sb.AppendLine("  },");
        }
        if (r.Body != null)
        {
            string body = J(r.Body);
            try
            {
                if ((r.Header("Content-Type") ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    var pretty = JsonTools.Format(JsonTools.Parse(r.Body)).Replace("\n", "\n  ");
                    body = $"JSON.stringify({pretty})";
                }
            }
            catch { /* keep raw string */ }
            sb.AppendLine($"  body: {body},");
        }
        if (!r.Follow) sb.AppendLine("  redirect: \"manual\",");
        sb.AppendLine("});");
        sb.AppendLine("console.log(response.status);");
        sb.AppendLine("console.log(await response.text());");
        return sb.ToString().TrimEnd();
    }

    public static string ToPython(CurlRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(Notes(r, "#"));
        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.AppendLine($"url = {J(r.Url)}");
        if (r.Headers.Count > 0)
        {
            sb.AppendLine("headers = {");
            foreach (var (n, v) in r.Headers) sb.AppendLine($"    {J(n)}: {J(v)},");
            sb.AppendLine("}");
        }
        if (r.Body != null) sb.AppendLine($"payload = {J(r.Body)}");
        var extra = new List<string>();
        if (r.Headers.Count > 0) extra.Add("headers=headers");
        if (r.Body != null) extra.Add("data=payload");
        if (r.User != null)
        {
            var i = r.User.IndexOf(':');
            extra.Add(i < 0 ? $"auth=({J(r.User)}, \"\")" : $"auth=({J(r.User[..i])}, {J(r.User[(i + 1)..])})");
        }
        if (r.Insecure) extra.Add("verify=False");
        if (!r.Follow) extra.Add("allow_redirects=False");
        sb.AppendLine();
        sb.AppendLine($"response = requests.request({J(r.Method)}, url{(extra.Count > 0 ? ", " + string.Join(", ", extra) : "")})");
        sb.AppendLine("print(response.status_code)");
        sb.AppendLine("print(response.text)");
        return sb.ToString().TrimEnd();
    }

    public static string ToPowerShell(CurlRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(Notes(r, "#"));
        var ct = r.Header("Content-Type");
        var headers = r.Headers.Where(h => !h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"    {Ps(h.Name)} = {Ps(h.Value)}").ToList();
        if (r.User != null)
            headers.Add($"    'Authorization' = ('Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes({Ps(r.User)})))");
        if (headers.Count > 0)
        {
            sb.AppendLine("$headers = @{");
            sb.AppendLine(string.Join("\n", headers));
            sb.AppendLine("}");
        }
        var m = char.ToUpperInvariant(r.Method[0]) + r.Method[1..].ToLowerInvariant();
        var cmd = new StringBuilder($"Invoke-RestMethod -Uri {Ps(r.Url)} -Method {m}");
        if (headers.Count > 0) cmd.Append(" -Headers $headers");
        if (ct != null) cmd.Append($" -ContentType {Ps(ct)}");
        if (r.Body != null) cmd.Append($" -Body {Ps(r.Body)}");
        if (r.Insecure) cmd.Append(" -SkipCertificateCheck");
        if (!r.Follow) cmd.Append(" -MaximumRedirection 0");
        sb.AppendLine(cmd.ToString());
        return sb.ToString().TrimEnd();
    }
}
