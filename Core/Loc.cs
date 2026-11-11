using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ASD;

/// <summary>
/// Languages. ASD's texts are English in the code; this class translates them on the screen.
///
///  • The words live in plain text files (Assets\Lang\*.lang.txt next to ASD.exe, and %LocalAppData%\ASD\lang\*.lang.txt for
///    your own): first line "#en || ar || fr ...", then one line per text "English || arabic || french ...".
///    Anyone can add a language or a missing text by adding such a file, with no new build.
///  • <see cref="Apply"/> walks the visible controls (labels, buttons, headers, tooltips, dialogs, menus ...) and swaps every text it
///    knows. The English text is remembered per control, so changing the language again works at once, and a text that the page
///    changes later (a status line) is translated again after the next layout (see <see cref="Track"/>).
///  • A text with a number or a name in it is a pattern: "Opening {0}…".
///  • Arabic (and other right-to-left languages) mirrors the window; text boxes stay left-to-right (code, URLs, hashes).
/// </summary>
internal static class Loc
{
    internal sealed record Language(string Code, string Name, bool Rtl);

    private sealed record Memo(string English, string Applied);

    private static readonly Dictionary<string, (string Name, bool Rtl)> Names = new()
    {
        ["en"] = ("English", false),
        ["ar"] = ("العربية", true),
        ["fr"] = ("Français", false),
        ["es"] = ("Español", false),
        ["de"] = ("Deutsch", false),
        ["tr"] = ("Türkçe", false),
        ["ru"] = ("Русский", false),
        ["zh"] = ("简体中文", false),
        ["ja"] = ("日本語", false),
        ["pt"] = ("Português", false),
        ["fa"] = ("فارسی", true),
        ["ur"] = ("اردو", true),
        ["he"] = ("עברית", true),
        ["it"] = ("Italiano", false),
        ["ko"] = ("한국어", false),
        ["hi"] = ("हिन्दी", false),
        ["id"] = ("Bahasa Indonesia", false),
        ["nl"] = ("Nederlands", false),
        ["pl"] = ("Polski", false),
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new();
    private static readonly List<Language> Langs = new();
    private static readonly ConditionalWeakTable<object, Dictionary<string, Memo>> Memos = new();
    private static bool _loaded;
    private static bool _used;   // a language other than English was on at some point in this run: keep walking so English can come back

    private static Dictionary<string, string>? _table;
    private static List<(Regex Rx, string Template)> _patterns = new();

    public static string Current { get; private set; } = "en";
    public static bool Rtl { get; private set; }

    /// <summary>False while English is on and was never left: then nothing is walked and nothing costs anything.</summary>
    public static bool Active => Current != "en" || _used;

    /// <summary>The saved choice: "auto" (the Windows language) or a language code.</summary>
    public static string Setting => SettingsStore.Get("Language", "auto");

    public static event Action? Changed;

    public static IReadOnlyList<Language> Available { get { EnsureLoaded(); return Langs; } }

    public static void Init()
    {
        EnsureLoaded();
        Resolve();
    }

    /// <summary>"auto" or a language code. Takes effect at once.</summary>
    public static void Set(string setting)
    {
        EnsureLoaded();
        SettingsStore.Set("Language", setting);
        Resolve();
        Changed?.Invoke();
    }

    // ───────── loading ─────────
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            LoadFolder(Path.Combine(AppContext.BaseDirectory, "Assets", "Lang"));
            LoadFolder(Path.Combine(AppPaths.DataDir, "lang"));   // yours: adds to / overrides the built-in texts
        }
        catch { /* no language files: English only */ }

        Langs.Add(new Language("en", Names["en"].Name, false));
        var codes = Tables.Where(t => t.Value.Count > 0).Select(t => t.Key).Where(c => c != "en").ToList();
        codes.Sort((a, b) => Order(a).CompareTo(Order(b)) != 0 ? Order(a).CompareTo(Order(b)) : string.CompareOrdinal(a, b));
        foreach (var c in codes)
        {
            var known = Names.TryGetValue(c, out var n);
            Langs.Add(new Language(c, known ? n.Name : c.ToUpperInvariant(), known && n.Rtl));
        }
    }

    private static int Order(string code)
    {
        var i = Array.IndexOf(new[] { "ar", "fr", "es", "de", "tr", "ru", "zh", "ja", "pt" }, code);
        return i < 0 ? 100 : i;
    }

    private static void LoadFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder, "*.lang.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            LoadFile(file);
    }

    private static void LoadFile(string path)
    {
        try
        {
            string[]? codes = null;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;

                if (line[0] == '#')   // "#en || ar || fr": which column is which language
                {
                    codes = line[1..].Split("||").Select(c => c.Trim().ToLowerInvariant()).ToArray();
                    continue;
                }
                if (line[0] == '@')   // "@xx || Native name || rtl": a language the program does not know
                {
                    var p = line[1..].Split("||").Select(x => x.Trim()).ToArray();
                    if (p.Length >= 2 && p[0].Length > 0)
                        Names[p[0].ToLowerInvariant()] = (p[1], p.Length > 2 && p[2].Equals("rtl", StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                if (codes == null) continue;

                var parts = line.Split("||");
                var key = parts[0].Trim();
                if (key.Length == 0) continue;
                for (var i = 1; i < codes.Length && i < parts.Length; i++)
                {
                    var value = parts[i].Trim();
                    if (value.Length == 0 || codes[i].Length == 0) continue;
                    if (!Tables.TryGetValue(codes[i], out var table)) Tables[codes[i]] = table = new Dictionary<string, string>(StringComparer.Ordinal);
                    table[key] = value;
                }
            }
        }
        catch { /* a broken file only loses its own texts */ }
    }

    private static void Resolve()
    {
        var setting = Setting;
        var code = "en";
        if (setting == "auto")
        {
            try { code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant(); } catch { code = "en"; }
        }
        else code = setting.ToLowerInvariant();
        if (code != "en" && !Tables.ContainsKey(code)) code = "en";

        Current = code;
        Rtl = code != "en" && Names.TryGetValue(code, out var n) && n.Rtl;
        if (code != "en") _used = true;

        _table = code != "en" && Tables.TryGetValue(code, out var t) ? t : null;
        _patterns = new List<(Regex, string)>();
        if (_table != null)
        {
            foreach (var kv in _table)
            {
                if (!kv.Key.Contains("{0}", StringComparison.Ordinal)) continue;
                try
                {
                    var rx = "^" + Regex.Escape(kv.Key).Replace("\\{0}", "(.+?)") + "$";
                    _patterns.Add((new Regex(rx, RegexOptions.CultureInvariant | RegexOptions.Singleline), kv.Value));
                }
                catch { /* a bad pattern is skipped */ }
            }
        }
    }

    // ───────── translating one text ─────────
    /// <summary>The text in the current language, or the same text when there is no translation.</summary>
    public static string T(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (_table == null) return s;
        if (_table.TryGetValue(s, out var hit)) return hit;

        foreach (var (rx, template) in _patterns)
        {
            var m = rx.Match(s);
            if (m.Success) return template.Replace("{0}", T(m.Groups[1].Value));
        }

        // "Search…" / "Name:" when only "Search" / "Name" is known
        string tail = s.EndsWith("…", StringComparison.Ordinal) ? "…"
                    : s.EndsWith("...", StringComparison.Ordinal) ? "..."
                    : s.EndsWith(":", StringComparison.Ordinal) ? ":" : "";
        if (tail.Length > 0 && s.Length > tail.Length && _table.TryGetValue(s[..^tail.Length].TrimEnd(), out var baseHit))
            return baseHit + tail;
        return s;
    }

    // ───────── applying to the screen ─────────
    private static HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private static int _budget;

    /// <summary>
    /// Keeps a window translated: after the layout has been quiet for a moment, every text under <paramref name="root"/> (and in the
    /// open dialogs, menus and flyouts) is translated; <paramref name="direction"/> (default: root) is mirrored for Arabic and the like.
    /// </summary>
    public static void Track(FrameworkElement root, FrameworkElement? direction = null)
    {
        var dirRoot = direction ?? root;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void Run()
        {
            timer.Stop();
            try { Refresh(root, dirRoot); } catch { /* cosmetic only */ }
        }
        timer.Tick += (_, _) => Run();
        root.LayoutUpdated += (_, _) => { if (Active && !timer.IsEnabled) timer.Start(); };

        Action changed = Run;
        Changed += changed;
        root.Unloaded += (_, _) => Changed -= changed;
        if (Active) timer.Start();
    }

    private static void Refresh(FrameworkElement root, FrameworkElement dirRoot)
    {
        var flow = Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (dirRoot.FlowDirection != flow) dirRoot.FlowDirection = flow;
        Apply(root);

        if (root.XamlRoot == null) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
        {
            if (popup.Child is not FrameworkElement child) continue;
            if (child.FlowDirection != flow) child.FlowDirection = flow;
            Apply(child);
        }
    }

    /// <summary>Translates (or, after a language change, re-translates) everything under <paramref name="root"/>.</summary>
    public static void Apply(DependencyObject? root)
    {
        if (root == null || !Active) return;
        _seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        _budget = 12000;   // a huge list must never freeze the window
        try { Walk(root); } catch { /* cosmetic only */ }
        _seen.Clear();
    }

    private static string Pick(object owner, string prop, string current)
    {
        Memos.TryGetValue(owner, out var map);
        var english = current;
        if (map != null && map.TryGetValue(prop, out var m) && m.Applied == current) english = m.English;   // still what we put there

        var shown = T(english);
        if (map == null)
        {
            if (shown == current) return current;   // nothing to translate: remember nothing
            map = new Dictionary<string, Memo>();
            Memos.Add(owner, map);
        }
        map[prop] = new Memo(english, shown);
        return shown;
    }

    private static void Str(object owner, string prop, string? current, Action<string> set)
    {
        if (string.IsNullOrEmpty(current)) return;
        var shown = Pick(owner, prop, current);
        if (shown != current) set(shown);
    }

    private static void Obj(object owner, string prop, object? current, Action<string> set)
    {
        if (current is string s) Str(owner, prop, s, set);
    }

    private static void Walk(object? o)
    {
        if (o is not DependencyObject d || _budget <= 0) return;
        if (!_seen.Add(d)) return;
        _budget--;

        if (d is TextBlock tb)
        {
            if (!tb.IsTextSelectionEnabled) Str(tb, "Text", tb.Text, v => tb.Text = v);   // selectable text is data (a result), not a label
            return;
        }

        if (d is TextBox box)
        {
            Obj(box, "Header", box.Header, v => box.Header = v);
            Str(box, "Ph", box.PlaceholderText, v => box.PlaceholderText = v);
            if (Rtl && box.FlowDirection != FlowDirection.LeftToRight) box.FlowDirection = FlowDirection.LeftToRight;   // code, URLs, hashes
        }
        else if (d is PasswordBox pw)
        {
            Obj(pw, "Header", pw.Header, v => pw.Header = v);
            Str(pw, "Ph", pw.PlaceholderText, v => pw.PlaceholderText = v);
            if (Rtl && pw.FlowDirection != FlowDirection.LeftToRight) pw.FlowDirection = FlowDirection.LeftToRight;
        }
        else if (d is ComboBox cb)
        {
            Obj(cb, "Header", cb.Header, v => cb.Header = v);
            Str(cb, "Ph", cb.PlaceholderText, v => cb.PlaceholderText = v);
        }
        else if (d is AutoSuggestBox sb)
        {
            Obj(sb, "Header", sb.Header, v => sb.Header = v);
            Str(sb, "Ph", sb.PlaceholderText, v => sb.PlaceholderText = v);
        }
        else if (d is NumberBox nb)
        {
            Obj(nb, "Header", nb.Header, v => nb.Header = v);
            Str(nb, "Ph", nb.PlaceholderText, v => nb.PlaceholderText = v);
        }
        else if (d is ToggleSwitch ts)
        {
            Obj(ts, "Header", ts.Header, v => ts.Header = v);
            Obj(ts, "On", ts.OnContent, v => ts.OnContent = v);
            Obj(ts, "Off", ts.OffContent, v => ts.OffContent = v);
        }
        else if (d is RadioButtons rbs)
        {
            Obj(rbs, "Header", rbs.Header, v => rbs.Header = v);
        }
        else if (d is Slider sl)
        {
            Obj(sl, "Header", sl.Header, v => sl.Header = v);
        }
        else if (d is InfoBar ib)
        {
            Str(ib, "Title", ib.Title, v => ib.Title = v);
            Str(ib, "Message", ib.Message, v => ib.Message = v);
            Walk(ib.ActionButton);
        }
        else if (d is ContentDialog dlg)
        {
            Obj(dlg, "Title", dlg.Title, v => dlg.Title = v);
            Str(dlg, "Primary", dlg.PrimaryButtonText, v => dlg.PrimaryButtonText = v);
            Str(dlg, "Secondary", dlg.SecondaryButtonText, v => dlg.SecondaryButtonText = v);
            Str(dlg, "Close", dlg.CloseButtonText, v => dlg.CloseButtonText = v);
        }
        else if (d is Expander ex)
        {
            Obj(ex, "Header", ex.Header, v => ex.Header = v);
        }
        else if (d is PivotItem pi)
        {
            Obj(pi, "Header", pi.Header, v => pi.Header = v);
        }
        else if (d is NavigationView nv)
        {
            foreach (var item in nv.MenuItems) Walk(item);
            foreach (var item in nv.FooterMenuItems) Walk(item);
        }

        if (d is ContentControl cc)
        {
            if (cc.Content is string text) Str(cc, "Content", text, v => cc.Content = v);
            else Walk(cc.Content);
        }
        if (d is ItemsControl ic)
        {
            foreach (var item in ic.Items) Walk(item);
        }
        if (d is Panel panel)
        {
            foreach (var child in panel.Children) Walk(child);
        }
        else if (d is Border border) Walk(border.Child);
        else if (d is Viewbox vb) Walk(vb.Child);
        else if (d is UserControl uc) Walk(uc.Content);
        else if (d is ContentPresenter cp)
        {
            if (cp.Content is string ptext) Str(cp, "Content", ptext, v => cp.Content = v);
        }

        if (d is Button button) WalkFlyout(button.Flyout);
        else if (d is SplitButton split) WalkFlyout(split.Flyout);
        if (d is UIElement element) WalkFlyout(element.ContextFlyout);

        if (d is FrameworkElement fe && ToolTipService.GetToolTip(fe) is string tip)
            Str(fe, "Tip", tip, v => ToolTipService.SetToolTip(fe, v));

        try   // the parts a control is made of (the title of a dialog, the header of a tab ...)
        {
            var count = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        catch { /* not a visual */ }
    }

    private static void WalkFlyout(FlyoutBase? flyout)
    {
        if (flyout is MenuFlyout menu) WalkMenu(menu.Items);
        else if (flyout is Flyout plain) Walk(plain.Content);
    }

    private static void WalkMenu(IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item is MenuFlyoutSubItem sub)
            {
                Str(sub, "Text", sub.Text, v => sub.Text = v);
                WalkMenu(sub.Items);
            }
            else if (item is MenuFlyoutItem mi)
            {
                Str(mi, "Text", mi.Text, v => mi.Text = v);
            }
        }
    }
}
