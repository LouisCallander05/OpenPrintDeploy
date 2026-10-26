using System.Security.AccessControl;
using System.Security.Principal;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;
using Xunit.Abstractions;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ServerStorageUpgradeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;CIID;FW;;;BU)", true)]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;CIID;0x116;;;BU)", true)]
    [InlineData("O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;CIID;0x100116;;;BU)", true)]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;CI;FW;;;BU)", true)]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;CIID;FA;;;BU)", false)]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;OICIID;FW;;;BU)", false)]
    [InlineData("O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;CIID;FW;;;AU)", false)]
    [InlineData("O:S-1-5-21-1-2-3-1001G:SYD:(A;;FA;;;SY)(A;CIID;FW;;;BU)", false)]
    public void Migration_OnlyAcceptsKnownLegacyDirectoryRule(string sddl, bool allowed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        // Ordinary service startup must still reject every one of these ACLs.
        Assert.Throws<IOException>(() => PrivateDirectory.ValidateAccess(security));
        if (allowed) PrivateDirectory.ValidateLegacyServerDirectoryAccess(security);
        else Assert.Throws<IOException>(() => PrivateDirectory.ValidateLegacyServerDirectoryAccess(security));
    }

    [Fact]
    public void MigrationCommand_RejectsNonSystemCallerBeforeTouchingStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem) return;
        Assert.Throws<UnauthorizedAccessException>(() => OpenPrintDeploy.Server.Data.ServerStorageUpgrade.Run());
    }

    [Fact]
    public void InstallerMigration_SecuresRealLegacyDirectory_PreservesFiles_AndIsRepeatable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // Hosted Windows CI is elevated. Make absence of this regression
            // coverage a CI failure, while permitting ordinary local dev runs.
            Assert.False(Environment.GetEnvironmentVariable("CI") == "true", "Elevated Windows CI is required for the filesystem migration test.");
            output.WriteLine("Filesystem migration test requires elevation; descriptor tests still run locally.");
            return;
        }
        var temp = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temp, "opd-upgrade-test-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(temp) + Path.DirectorySeparatorChar, root);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        try
        {
            var legacyParent = new DirectorySecurity();
            legacyParent.SetSecurityDescriptorSddlForm("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICIIO;GA;;;CO)(A;OICI;FRFX;;;BU)(A;CI;FW;;;BU)");
            new DirectoryInfo(root).Create(legacyParent);
            var data = System.IO.Directory.CreateDirectory(Path.Combine(root, "OpenPrintDeploy"));
            var directoryAcl = data.GetAccessControl();
            directoryAcl.SetOwner(admins);
            data.SetAccessControl(directoryAcl);
            var file = new FileInfo(Path.Combine(data.FullName, "app.db"));
            File.WriteAllText(file.FullName, "existing database canary");
            var fileAcl = file.GetAccessControl();
            fileAcl.SetOwner(admins);
            file.SetAccessControl(fileAcl);

            Assert.Throws<IOException>(() => { PrivateDirectory.Create(data.FullName); });
            output.WriteLine("Legacy directory descriptor: " + data.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All & ~AccessControlSections.Audit));
            foreach (FileSystemAccessRule rule in data.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                output.WriteLine($"{rule.IdentityReference}: rights={rule.FileSystemRights}, inherited={rule.IsInherited}, inheritance={rule.InheritanceFlags}, propagation={rule.PropagationFlags}");
            PrivateDirectory.CreateForServerUpgrade(data.FullName);
            PrivateDirectory.ValidateExistingFile(file.FullName);
            Assert.Equal("existing database canary", File.ReadAllText(file.FullName));
            Assert.True(data.GetAccessControl().AreAccessRulesProtected);
            PrivateDirectory.Create(data.FullName);
            PrivateDirectory.CreateForServerUpgrade(data.FullName);

            // Securing a directory must not bless an explicitly writable file.
            fileAcl = file.GetAccessControl();
            fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Write, AccessControlType.Allow));
            file.SetAccessControl(fileAcl);
            PrivateDirectory.CreateForServerUpgrade(data.FullName);
            Assert.Throws<IOException>(() => PrivateDirectory.ValidateExistingFile(file.FullName));
            Assert.Equal("existing database canary", File.ReadAllText(file.FullName));
            output.WriteLine("Real legacy ACL migration, retained contents, idempotence and writable-file rejection passed.");
        }
        finally
        {
            // Only this test's verified, newly-created temporary tree is removed.
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
