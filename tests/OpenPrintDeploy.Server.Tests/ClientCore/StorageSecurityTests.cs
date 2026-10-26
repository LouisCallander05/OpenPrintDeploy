using System.Security.AccessControl;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class StorageSecurityTests
{
    [Theory]
    [InlineData("O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)", true)]
    [InlineData("O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)", true)]
    [InlineData("O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BU)", false)]
    [InlineData("O:SYG:SYD:P(A;;FA;;;SY)(A;;FW;;;AU)", false)]
    [InlineData("O:S-1-5-21-1-2-3-1001G:SYD:P(A;;FA;;;SY)", false)]
    public void ProtectedStorage_RejectsUntrustedOwnerOrWriter(string sddl, bool allowed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        if (allowed) PrivateDirectory.ValidateAccess(security);
        else Assert.Throws<IOException>(() => { if (OperatingSystem.IsWindows()) PrivateDirectory.ValidateAccess(security); });
    }
}
