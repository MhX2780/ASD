using System.Text.Json;

namespace ASD;

/// <summary>One key found in pasted text.</summary>
internal sealed record ImportItem(string Name, string Value, string Service, string Notes);

/// <summary>Reads keys from a pasted <c>.env</c> file or JSON.</summary>
internal static class KeyImport
{
    public const int MaxItems = 500;

    /// <summary>JSON when the text starts with { or [, otherwise .env lines. Throws <see cref="JsonException"/> for broken JSON.</summary>
    public static List<ImportItem> Parse(string? text)
    {
        text = (text ?? "").Trim('﻿', ' ', '\t', '\r', '\n');
        if (text.Length == 0) return new List<ImportItem>();
        var items = text[0] is '{' or '[' ? ParseJson(text) : ParseEnv(text);
        return items.Take(MaxItems).ToList();
    }

    // NAME=value, optional "export ", quotes, # comments. A value cannot span several lines.
    private static List<ImportItem> ParseEnv(string text)
    {
        var items = new List<ImportItem>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length > 0 && value[0] is '"' or '\'')
            {
                var quote = value[0];
                var end = value.IndexOf(quote, 1);
                value = end > 0 ? value[1..end] : value[1..];
            }
            else
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);   // an inline comment
                if (hash >= 0) value = value[..hash].TrimEnd();
            }
            if (name.Length > 0 && value.Length > 0) items.Add(new ImportItem(name, value, "", ""));
        }
        return items;
    }

    // { "NAME": "value", "group": { "NAME": "value" } }  or  [ { "name": "...", "value": "..." } ]
    private static List<ImportItem> ParseJson(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var items = new List<ImportItem>();
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = Field(el, "name", "key", "title", "id", "label");
                var value = Field(el, "value", "secret", "token", "apikey", "api_key", "password");
                if (name.Length > 0 && value.Length > 0)
                    items.Add(new ImportItem(name, value, Field(el, "service"), Field(el, "notes", "note", "description")));
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            Flatten(root, "", 0, items);
        }
        return items;
    }

    private static void Flatten(JsonElement obj, string prefix, int depth, List<ImportItem> items)
    {
        foreach (var p in obj.EnumerateObject())
        {
            var name = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.String:
                    if (p.Value.GetString() is { Length: > 0 } s) items.Add(new ImportItem(name, s, "", ""));
                    break;
                case JsonValueKind.Number:
                    items.Add(new ImportItem(name, p.Value.GetRawText(), "", ""));
                    break;
                case JsonValueKind.Object when depth < 4:
                    Flatten(p.Value, name, depth + 1, items);
                    break;
            }
        }
    }

    private static string Field(JsonElement obj, params string[] names)
    {
        foreach (var wanted in names)
            foreach (var p in obj.EnumerateObject())
                if (string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                    if (text.Length > 0) return text;
                }
        return "";
    }
}
