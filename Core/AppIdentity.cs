namespace ASD;

/// <summary>
/// Is ASD running as an installed MSIX package (installed by ASD.Setup) or as a plain folder
/// (a test run, the old zip install)? The two update in different ways:
///   packaged    -> ASD downloads ASD.msix and Windows installs it (all or nothing, never half-updated)
///   not packaged -> the old way: ASD.zip is copied over the folder by a small .bat
/// </summary>
internal static class AppIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>"Publisher-independent" package name, e.g. AdvaASD.ASD_8wekyb3d8bbwe. Null when not packaged.</summary>
    public static string? FamilyName { get; } = ReadFamilyName();

    /// <summary>The installed package version (1.0.&lt;build&gt;.0). Null when not packaged.</summary>
    public static Version? PackageVersion { get; } = ReadVersion();

    /// <summary>What "start" must open to start ASD again: the package for an installed ASD, otherwise the exe.</summary>
    public static string? LaunchTarget
        => IsPackaged && FamilyName != null ? $@"shell:AppsFolder\{FamilyName}!App" : Environment.ProcessPath;

    private static bool DetectPackaged()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch { return false; }
    }

    private static string? ReadFamilyName()
    {
        if (!IsPackaged) return null;
        try
        {
            int length = 0;
            GetCurrentPackageFamilyName(ref length, null);
            if (length <= 0) return null;
            var sb = new StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    private static Version? ReadVersion()
    {
        if (!IsPackaged) return null;
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(v.Major, v.Minor, v.Build, v.Revision);
        }
        catch { return null; }
    }
}
