using System.Text.Json;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class CoreDriverPreparationTests
{
    private static readonly Guid Unidrv = new("D20EA372-DD35-4950-9ED8-A6335AFE79F0");
    private static readonly Guid Pclxl = new("D20EA372-DD35-4950-9ED8-A6335AFE79F2");
    private static readonly CoreDriverPackage Core = new(Unidrv, 33822, 123, "core/ntprint.inf")
    { PackageId = "ntprint.inf_amd64_0123456789abcdef" };

    [Theory]
    [InlineData(33648, 123, false)]
    [InlineData(33822, 123, true)]
    [InlineData(34000, 123, true)]
    [InlineData(34000, 122, false)]
    [InlineData(33648, 124, true)]
    public void EveryCorePackageMustMeetServerDateAndVersion(ulong version, long date, bool ready)
    {
        void Verify() => CoreDriverRequirements.Verify([Unidrv], [Core.Identity], [new(Unidrv, version, date)]);
        if (ready) Verify();
        else Assert.Throws<InvalidOperationException>(Verify);
    }

    [Fact]
    public void MissingAndUnenumeratedDependenciesCannotBeReportedReady()
    {
        Assert.Throws<InvalidOperationException>(() => CoreDriverRequirements.Verify([Unidrv], null, [Core.Identity]));
        Assert.Throws<InvalidOperationException>(() => CoreDriverRequirements.Verify([Unidrv], [], [Core.Identity]));
        Assert.Throws<InvalidOperationException>(() => CoreDriverRequirements.Verify([Unidrv], [Core.Identity], []));
        Assert.Throws<InvalidOperationException>(() => CoreDriverRequirements.Verify([Unidrv, Pclxl], [Core.Identity], [Core.Identity]));
        CoreDriverRequirements.Verify([], null, []); // older servers remain usable for self-contained drivers
    }

    [Fact]
    public void InvalidOrAmbiguousDependencyMetadataIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CoreDriverRequirements.Validate([Core.Identity, Core.Identity]));
        Assert.Throws<InvalidDataException>(() => CoreDriverRequirements.Validate([Core.Identity with { Id = Guid.Empty }]));
        Assert.Throws<InvalidDataException>(() => CoreDriverRequirements.Validate([Core.Identity with { Version = 0 }]));
    }

    [Fact]
    public void SeparateCorePackageIsStagedAndVerifiedBeforeVendorInstallation()
        => WithPackage((root, manifest) =>
        {
            var events = new List<string>();
            DriverPackageInstallation.InstallCore(manifest, root,
                path => { events.Add("stage:" + Path.GetFileName(path)); return path; },
                (_, _) => { Assert.Contains("verify-core", events); events.Add("vendor"); },
                _ => Driver(), _ =>
                {
                    events.Add("verify-core");
                    return true;
                });
            Assert.Equal(new[] { "stage:ntprint.inf", "verify-core", "verify-core", "vendor", "verify-core", "verify-core" }, events);
        });

    [Fact]
    public void SignatureOrStagingFailureStopsBeforeVendorInstallation()
        => WithPackage((root, manifest) =>
        {
            var installed = false;
            Assert.Throws<IOException>(() => DriverPackageInstallation.InstallCore(manifest, root,
                _ => throw new IOException("signature rejected"), (_, _) => installed = true,
                _ => Driver(), _ => true));
            Assert.False(installed);
        });

    [Fact]
    public void SuccessfulUploadWithoutAnAvailableSignedPackageCannotProceed()
        => WithPackage((root, manifest) =>
        {
            var installed = false;
            Assert.Throws<InvalidOperationException>(() => DriverPackageInstallation.InstallCore(manifest, root,
                path => path, (_, _) => installed = true, _ => Driver(), _ => false));
            Assert.False(installed);
        });

    [Fact]
    public void SharedPackageIsUploadedOnceAndBothCoreModelsAreVerified()
        => WithPackage((root, manifest) =>
        {
            var stages = 0;
            DriverPackageInstallation.InstallCore(manifest, root, path => { stages++; return path; }, (_, _) => { },
                _ => Driver(), _ => true);
            Assert.Equal(1, stages);
        });

    [Fact]
    public void CoreManifestRoundTripsWithoutChangingLegacyFields()
    {
        var manifest = Manifest();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(manifest, options);
        var copy = JsonSerializer.Deserialize<DriverManifest>(json, options)!;
        Assert.Equal(manifest.InfPath, copy.InfPath);
        Assert.Equal(manifest.CoreDrivers, copy.CoreDrivers);
        var old = JsonSerializer.Deserialize<DriverManifest>("{\"driverName\":\"test\"}", options)!;
        Assert.Null(old.CoreDrivers);
    }

    private static InstalledDriver Driver() => new("Lexmark", "Windows x64", 1, 2, "vendor.inf")
    { CoreDependencies = [Unidrv, Pclxl] };

    private static DriverManifest Manifest() => new("\\\\server\\queue", "Lexmark", "Windows x64", 1, 2,
        "vendor.inf", new string('A', 64), 1) { CoreDrivers = [Core, Core with { Id = Pclxl }] };

    private static void WithPackage(Action<string, DriverManifest> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "opd-core-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(root, "core"));
        try
        {
            File.WriteAllText(Path.Combine(root, "vendor.inf"), "test");
            File.WriteAllText(Path.Combine(root, "core", "ntprint.inf"), "test");
            test(root, Manifest());
        }
        finally { System.IO.Directory.Delete(root, true); }
    }
}
