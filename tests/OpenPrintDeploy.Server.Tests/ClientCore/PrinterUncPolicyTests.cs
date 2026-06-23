using OpenPrintDeploy.Client.Core;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class PrinterUncPolicyTests
{
    private static readonly string[] Allowed = ["printsrv01.corp.local"];

    [Theory]
    [InlineData(@"\\printsrv01.corp.local\HR Printer")]
    [InlineData(@"\\printsrv01\HR Printer")] // short name matches the allowed FQDN
    public void AllowsWellFormedUncFromAllowedHost(string unc)
    {
        Assert.True(PrinterUncPolicy.IsAllowed(unc, Allowed, out _));
    }

    [Theory]
    [InlineData("notaunc")]
    [InlineData(@"\\onlyhost")]
    [InlineData(@"\\host\")]
    [InlineData(@"\\\share")]
    [InlineData(@"\\host\share\..\admin$")]
    [InlineData(@"\\ho st\share")]
    public void RejectsMalformedUnc(string unc)
    {
        Assert.False(PrinterUncPolicy.IsAllowed(unc, Allowed, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void RejectsHostNotOnAllowList()
    {
        Assert.False(PrinterUncPolicy.IsAllowed(@"\\evil.attacker.com\share", Allowed, out var reason));
        Assert.Contains("not an allowed print server", reason);
    }

    [Fact]
    public void EmptyAllowList_EnforcesFormatOnly()
    {
        Assert.True(PrinterUncPolicy.IsAllowed(@"\\anyhost\share", [], out _));
        Assert.False(PrinterUncPolicy.IsAllowed("garbage", [], out _));
    }

    [Fact]
    public void DoesNotShortMatchDifferentIps()
    {
        // 192.* must not collapse to a short label and match a different 192.* host.
        Assert.False(PrinterUncPolicy.IsAllowed(@"\\192.168.1.5\share", ["192.5.5.5"], out _));
        Assert.True(PrinterUncPolicy.IsAllowed(@"\\192.168.1.5\share", ["192.168.1.5"], out _));
    }
}
