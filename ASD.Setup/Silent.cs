namespace ASD.Setup;

/// <summary>
/// ASD-Setup.exe --silent [--start] [--log FILE]: installs ASD without any window (tests, scripts, managed PCs).
/// Exit code 0 = ASD is installed, 1 = it failed (the reason is in the log).
/// </summary>
internal static class Silent
{
    public static async Task RunAsync(bool start)
    {
        int code = 0;
        try
        {
            Installer.Log("silent install started");
            Installer.CheckWindows();
            var certificate = Installer.ReadCertificate()
                ?? throw new SetupException("This installer was built without ASD's certificate.");

            var progress = new Progress<(double Fraction, string Text)>(_ => { });
            var package = Installer.LocalPackage() ?? await Installer.DownloadAsync(progress, CancellationToken.None);
            Installer.Log("package: " + package);

            await Installer.TrustAsync(certificate, CancellationToken.None);
            Installer.Log("the publisher is trusted");

            await Installer.InstallAsync(package, progress);
            var installed = Installer.FindInstalled()
                ?? throw new SetupException("Windows reported success, but ASD is not installed.");
            Installer.Log($"installed: ASD {Installer.VersionOf(installed)}");

            Installer.CopyOfflineExtensions();
            Installer.RegisterFileType();

            if (start) Installer.Launch();
        }
        catch (Exception e)
        {
            Installer.Log("FAILED: " + e.Message);
            code = 1;
        }

        Installer.Log($"exit code {code}");
        Environment.Exit(code);
    }
}
