using Microsoft.UI.Xaml;

namespace ASD.Extensibility;

/// <summary>Implemented once per extension DLL. ASD finds the class, creates it and calls <see cref="Register"/> at start-up.</summary>
public interface IAsdExtension
{
    /// <summary>Same value as "id" in extension.json, e.g. "MhX2780.HelloTool".</summary>
    string Id { get; }

    /// <summary>Called on the UI thread. Add pages (and anything else) through <paramref name="host"/>.</summary>
    void Register(IAsdHost host);
}

/// <summary>What ASD offers to an extension. Only add members here, never change or remove one (ApiVersion stays compatible).</summary>
public interface IAsdHost
{
    /// <summary>The version of this contract (1 for now).</summary>
    int ApiVersion { get; }

    /// <summary>The version of ASD that is running.</summary>
    Version AsdVersion { get; }

    /// <summary>A folder that belongs to this extension alone (created on demand): put its settings and data here.</summary>
    string DataFolder { get; }

    /// <summary>Adds a page to the navigation bar or to More tools (the person decides where, see placement in the install window).</summary>
    void AddPage(ExtensionPage page);

    /// <summary>Shows an ASD notification (the bell and, when ASD is in the background, a Windows toast).</summary>
    void Notify(string title, string message);
}

/// <summary>A page an extension adds. Build its UI in code (no XAML files).</summary>
public sealed class ExtensionPage
{
    /// <summary>Unique, short, lower-case. ASD prefixes it with the extension id.</summary>
    public required string Tag { get; init; }

    /// <summary>The text in the navigation bar and the header of the page.</summary>
    public required string Title { get; init; }

    /// <summary>A Segoe Fluent Icons character such as ""; null = the glyph from extension.json.</summary>
    public string? Glyph { get; init; }

    /// <summary>Called when the person opens the page; return the content (a StackPanel, a Grid, ...).</summary>
    public required Func<FrameworkElement> Create { get; init; }
}
