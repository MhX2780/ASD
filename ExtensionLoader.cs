using System.Runtime.Loader;
using ASD.Extensibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

/// <summary>
/// Starts the installed extensions when ASD starts: loads each DLL, finds its IAsdExtension and calls Register,
/// which adds the extension's pages (they appear in the navigation bar or in the Toolbox, as the person chose).
/// An extension that fails, or that closes ASD while it starts, is switched off and the reason is shown in the Extensions window.
/// </summary>
internal static class ExtensionLoader
{
    private static readonly Dictionary<string, string> Errors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static string? ErrorOf(string id) => Errors.TryGetValue(id, out var e) ? e : null;
    public static bool IsLoaded(string id) => Loaded.Contains(id);
    public static bool IsDisabled(string id) => SettingsStore.Get("ext.disabled." + id, "") == "1";

    /// <summary>Switches an extension on again (it starts at the next start of ASD).</summary>
    public static void Enable(string id)
    {
        SettingsStore.Set("ext.disabled." + id, "");
        Errors.Remove(id);
    }

    /// <summary>Called once, on the UI thread, before the main window is built.</summary>
    public static void LoadAll()
    {
        try
        {
            ExtensionStore.ApplyPending();

            // ASD was closed while an extension was starting last time: that extension is switched off
            var crashed = SettingsStore.Get("ext.loading", "");
            if (crashed.Length > 0)
            {
                SettingsStore.Set("ext.disabled." + crashed, "1");
                SettingsStore.Set("ext.loading", "");
                Errors[crashed] = "ASD closed while this extension was starting, so it was switched off.";
            }

            foreach (var dir in ExtensionStore.InstalledDirs()) LoadOne(dir);
        }
        catch { /* extensions are optional: ASD opens without them */ }
    }

    private static void LoadOne(string dir)
    {
        var info = ExtensionStore.ReadInstalled(dir);
        var id = info.Id;
        try
        {
            if (IsDisabled(id)) { Errors.TryAdd(id, "Switched off."); return; }
            if (info.ApiVersion > ExtensionStore.SupportedApiVersion)
            { Errors[id] = $"It needs extension API {info.ApiVersion}; this ASD understands up to {ExtensionStore.SupportedApiVersion}. Update ASD."; return; }
            if (info.MinAsdVersion.Length > 0 && ExtensionStore.IsNewer(info.MinAsdVersion, ExtensionStore.AsdVersion.ToString()))
            { Errors[id] = $"It needs ASD {info.MinAsdVersion} or newer."; return; }

            var dll = Path.Combine(dir, info.AssemblyFile.Length > 0 ? info.AssemblyFile : id + ".dll");
            if (!File.Exists(dll)) { Errors[id] = "Its DLL file is missing: " + Path.GetFileName(dll); return; }

            SettingsStore.Set("ext.loading", id);           // cleared below; still set after a crash
            var context = new AssemblyLoadContext("extension:" + id);   // its own references fall back to ASD's (the contract, Windows App SDK)
            var assembly = context.LoadFromAssemblyPath(dll);
            var type = assembly.GetTypes().FirstOrDefault(t => !t.IsAbstract && typeof(IAsdExtension).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                       ?? throw new InvalidOperationException("The DLL has no class that implements IAsdExtension.");
            var extension = (IAsdExtension)Activator.CreateInstance(type)!;
            extension.Register(new Host(info));
            Loaded.Add(id);
        }
        catch (Exception ex)
        {
            Errors[id] = ex.GetBaseException().Message;
        }
        finally
        {
            SettingsStore.Set("ext.loading", "");
        }
    }

    /// <summary>Restarts ASD (a new copy opens a moment after this one closes).</summary>
    public static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { /* the person can open ASD again by hand */ }
        Application.Current.Exit();
    }

    private sealed class Host : IAsdHost
    {
        private readonly ExtInfo _info;
        private string? _firstTag;

        public Host(ExtInfo info) { _info = info; }

        public int ApiVersion => ExtensionStore.SupportedApiVersion;
        public Version AsdVersion => ExtensionStore.AsdVersion;

        public string DataFolder
        {
            get
            {
                var d = AppPaths.File(Path.Combine("extensions", "data", _info.Id));
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public void AddPage(ExtensionPage page)
        {
            var tag = (_info.Id + "." + page.Tag).ToLowerInvariant();
            _firstTag ??= tag;
            var placement = ExtensionStore.PlacementOf(_info);
            var glyph = string.IsNullOrEmpty(page.Glyph) ? _info.Glyph : page.Glyph;
            ToolRegistry.AddExtension(new ToolInfo(tag, page.Title, _info.Description, glyph, placement,
                $"{page.Title} {_info.Name} {_info.Id} extension", () => new ExtensionHostPage(tag, page)));
        }

        public void Notify(string title, string message)
        {
            try { Notifier.Post(NoteKind.Info, title, message, _firstTag ?? "home", always: true); }
            catch { /* notifications are optional */ }
        }
    }
}

/// <summary>Shows the content an extension built, under the usual page header (title, scrolling body, status line).</summary>
internal sealed class ExtensionHostPage : ToolPage
{
    public ExtensionHostPage(string tag, ExtensionPage page) : base("ext." + tag, page.Title, "")
    {
        try { Body.Children.Add(page.Create()); }
        catch (Exception ex)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "This extension's page could not be shown: " + ex.GetBaseException().Message,
                TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            });
        }
    }
}
