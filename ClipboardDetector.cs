using System.Text.RegularExpressions;

namespace ASD;

/// <summary>Guesses which tool fits the text currently on the clipboard.</summary>
public static class ClipboardDetector
{
    private static readonly Regex Jwt = new(@"^(?:[Bb]earer\s+)?eyJ[\w-]+\.[\w-]+\.[\w-]*$");
    private static readonly Regex Epoch = new(@"^\d{10}(?:\d{3})?$");
    private static readonly Regex Cron = new(@"^(?:[\d*/,\-?]+\s+){4}[\d*/,\-?]+$");
    private static readonly Regex Url = new(@"^https?://\S+\?\S+$", RegexOptions.IgnoreCase);

    public static (string Tag, string Label)? Detect(string t)
    {
        if (t.Length == 0 || t.Length > 100_000) return null;
        if (Jwt.IsMatch(t)) return ("jwt", "Clipboard contains a JWT");
        if (t.StartsWith("curl ", StringComparison.OrdinalIgnoreCase)) return ("curl", "Clipboard contains a cURL command");
        if (Epoch.IsMatch(t)) return ("time", "Clipboard looks like a Unix timestamp");
        if ((t.StartsWith("{") && t.EndsWith("}")) || (t.StartsWith("[") && t.EndsWith("]")))
        {
            try { JsonTools.Parse(t); return ("json", "Clipboard contains JSON"); } catch { }
        }
        if (t.Length < 60 && Cron.IsMatch(t) && (t.Contains('*') || t.Contains('/'))) return ("cron", "Clipboard looks like a cron expression");
        if (Url.IsMatch(t)) return ("url", "Clipboard contains a URL with query parameters");
        return null;
    }
}
