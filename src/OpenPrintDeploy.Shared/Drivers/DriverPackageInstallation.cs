using System.Runtime.Versioning;

namespace OpenPrintDeploy.Shared.Drivers;

public static class DriverPackageInstallation
{
    [SupportedOSPlatform("windows")]
    public static void Install(DriverManifest manifest, string root)
        => InstallCore(manifest, root, WindowsDriverStore.Stage, WindowsDriverStore.Install,
            WindowsDriverStore.Find, WindowsDriverStore.IsCorePackageAvailable);

    internal static void InstallCore(DriverManifest manifest, string root,
        Func<string, string> stage, Action<string, string> install,
        Func<string, InstalledDriver?> find, Func<CoreDriverPackage, bool> isAvailable)
    {
        var expected = manifest.CoreDrivers;
        if (expected is not null) CoreDriverRequirements.Validate(expected.Select(d => d.Identity).ToList());
        var inf = ResolveInf(root, manifest.InfPath);
        foreach (var path in expected?.Select(d => d.InfPath).Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
            stage(ResolveInf(root, path));
        // Verify staging itself, before touching the vendor driver's binding.
        if (expected is { Count: > 0 })
            CoreDriverRequirements.Verify(expected.Select(d => d.Id).ToList(), expected.Select(d => d.Identity).ToList(),
                expected.Where(isAvailable).Select(d => d.Identity).ToList());
        install(inf, manifest.DriverName);
        var driver = find(manifest.DriverName) ?? throw new IOException("The installed driver could not be verified.");
        if (driver.Environment != manifest.Environment || driver.Version != manifest.Version || driver.Date != manifest.Date)
            throw new IOException("Installed driver does not match the print server version.");
        CoreDriverRequirements.Verify(driver.CoreDependencies, expected?.Select(d => d.Identity).ToList(),
            expected?.Where(isAvailable).Select(d => d.Identity).ToList() ?? []);
    }

    private static string ResolveInf(string root, string path)
    {
        var inf = DriverPackageFiles.Resolve(root, path);
        if (!inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) || !File.Exists(inf))
            throw new InvalidDataException("Driver package INF is missing or invalid.");
        return inf;
    }
}
