using System.IO.Compression;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace ASD;

/// <summary>
/// Brand / developer logos from the Simple Icons project (https://simpleicons.org, CC0 1.0).
/// Assets\simple-icons.json.gz holds { slug: [title, svgPathData] } for ~3 500 logos (GitHub, Google Gemini, Docker …).
/// A key's icon is stored as "si:&lt;slug&gt;"; the logo is drawn as a vector Path, so it takes the app's text colour.
/// </summary>
internal static class SimpleIcons
{
    public const string Prefix = "si:";

    internal readonly record struct Brand(string Slug, string Title, string Data);

    private static readonly object Gate = new();
    private static List<Brand>? _all;
    private static Dictionary<string, Brand>? _bySlug;

    /// <summary>Every logo, sorted by title. Loaded once, on first use (takes well under a second).</summary>
    public static IReadOnlyList<Brand> All
    {
        get { Load(); return _all!; }
    }

    public static Brand? Find(string slug)
    {
        Load();
        return _bySlug!.TryGetValue(slug, out var b) ? b : null;
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_all != null) return;
            var list = new List<Brand>();
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "Assets", "simple-icons.json.gz");
                if (File.Exists(file))
                {
                    using var fs = File.OpenRead(file);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    using var doc = JsonDocument.Parse(gz);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var title = p.Value[0].GetString() ?? p.Name;
                        var data = p.Value[1].GetString() ?? "";
                        if (data.Length > 0) list.Add(new Brand(p.Name, title, data));
                    }
                }
            }
            catch { /* a missing or damaged icon file only means "no brand logos" */ }

            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            _bySlug = new Dictionary<string, Brand>();
            foreach (var b in list) _bySlug[b.Slug] = b;
            _all = list;
        }
    }

    /// <summary>The logo as a size × size vector shape filled with <paramref name="fill"/>; null when the slug is unknown.</summary>
    public static FrameworkElement? Create(string slug, double size, Brush fill)
    {
        var brand = Find(slug);
        if (brand == null) return null;
        try
        {
            // "F1" = non-zero fill rule, like SVG (the XAML default would be even-odd and punch holes in some logos)
            var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), "F1 " + brand.Value.Data);
            return new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry, Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size,
            };
        }
        catch { return null; }
    }
}
