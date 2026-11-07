using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace ASD;

/// <param name="Tool">A tool to open ("keys", "automation", "updates" ...), from a Jump List item.</param>
/// <param name="FilePath">An .automation file the person opened with ASD.</param>
internal sealed record LaunchRequest(string? Tool, string? FilePath);

/// <summary>
/// What a start of the installed ASD asks for. The first start reads its own activation; when ASD is already open, a second
/// start (a Jump List item, a double-clicked .automation file) is handed to the open one (see App.OnLaunched) and arrives in <see cref="Deliver"/>.
/// </summary>
internal static class LaunchRouter
{
    private static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase)
        { "home", "keys", "automation", "extensions", "upload", "video", "updates", "settings" };

    public static LaunchRequest Read(AppActivationArguments? args)
    {
        try
        {
            if (args == null) return new LaunchRequest(null, null);
            switch (args.Kind)
            {
                case ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs file:
                    var path = file.Files.FirstOrDefault()?.Path;
                    return new LaunchRequest(null, string.IsNullOrEmpty(path) ? null : path);

                case ExtendedActivationKind.Launch when args.Data is ILaunchActivatedEventArgs launch:
                    return new LaunchRequest(ToolFrom(launch.Arguments), FileFrom(launch.Arguments));
            }
        }
        catch { /* an unreadable activation is just a normal start */ }
        return new LaunchRequest(null, null);
    }

    /// <summary>"C:\a\b.automation" (what the file association passes through the asd.exe alias) -> the path, when that file exists.</summary>
    private static string? FileFrom(string? arguments)
    {
        var text = (arguments ?? "").Trim();
        if (text.Length == 0 || text.StartsWith("--")) return null;
        var path = text.StartsWith('"') ? text.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : text;
        return path != null && path.EndsWith(".automation", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    /// <summary>"--open keys" (what the Jump List items pass) -> "keys".</summary>
    private static string? ToolFrom(string? arguments)
    {
        var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var at = Array.FindIndex(parts, p => p.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= parts.Length) return null;
        return Tools.Contains(parts[at + 1]) ? parts[at + 1].ToLowerInvariant() : null;
    }

    /// <summary>A second start was handed over to this running ASD (any thread).</summary>
    public static void Deliver(AppActivationArguments args)
    {
        var request = Read(args);
        if (App.MainWindow is MainWindow window)
            window.DispatcherQueue.TryEnqueue(() => window.HandleLaunch(request));
    }
}
