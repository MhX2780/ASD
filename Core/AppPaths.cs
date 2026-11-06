namespace ASD;

/// <summary>
/// Start-up switches:
///   --data-dir &lt;folder&gt;   keep ALL app data (settings, Keys Store vault, notification log, update work folder) in that folder
///                          instead of %LocalAppData%\ASD. The test script uses it so a test never touches real data.
///   --github-actions       "this run is the automated test on GitHub": no start-up update prompt and no clipboard banner,
///                          because nobody is there to answer them.
/// </summary>
internal static class AppPaths
{
    public static readonly bool GithubActions;
    public static readonly string DataDir;

    static AppPaths()
    {
        string? custom = null;
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--github-actions", StringComparison.OrdinalIgnoreCase)) GithubActions = true;
                else if (a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) custom = args[++i];
                else if (a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase)) custom = a["--data-dir=".Length..];
            }
        }
        catch { /* no switches */ }

        DataDir = !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom.Trim().Trim('"'))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD");
    }

    public static string File(string name) => Path.Combine(DataDir, name);
}
