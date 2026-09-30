using System.Text.Json;

namespace ASD;

/// <summary>
/// Tiny settings store backed by a JSON file in %LocalAppData%\ASD.
/// (Windows.Storage.ApplicationData.LocalSettings throws in unpackaged apps.)
/// </summary>
internal static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ASD", "settings.json");

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
        }
        catch { /* corrupted file: start fresh */ }
        return _cache ??= new Dictionary<string, string>();
    }

    public static string Get(string key, string defaultValue)
    {
        lock (Gate)
            return Load().TryGetValue(key, out var v) ? v : defaultValue;
    }

    public static void Set(string key, string value)
        => SetMany(new[] { new KeyValuePair<string, string>(key, value) });

    public static void SetMany(IEnumerable<KeyValuePair<string, string>> items)
    {
        lock (Gate)
        {
            var dict = Load();
            foreach (var kv in items) dict[kv.Key] = kv.Value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(dict));
            }
            catch { /* never crash because settings could not be saved */ }
        }
    }
}
