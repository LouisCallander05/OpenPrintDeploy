using OpenPrintDeploy.Server.Spooler;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Spooler;

public sealed class SpoolerUncTests
{
    [Fact]
    public void BuildsStandardUnc()
    {
        Assert.Equal(@"\\printsrv01\HR-MFP-01", SpoolerUnc.Build("printsrv01", "HR-MFP-01"));
    }

    [Theory]
    [InlineData(@"\\printsrv01")]
    [InlineData("printsrv01\\")]
    [InlineData("  printsrv01  ")]
    public void TrimsServerSlashesAndWhitespace(string server)
    {
        Assert.Equal(@"\\printsrv01\HR-MFP-01", SpoolerUnc.Build(server, "HR-MFP-01"));
    }

    [Theory]
    [InlineData("\\HR-MFP-01")]
    [InlineData("HR-MFP-01\\")]
    [InlineData("  HR-MFP-01  ")]
    public void TrimsShareSlashesAndWhitespace(string share)
    {
        Assert.Equal(@"\\printsrv01\HR-MFP-01", SpoolerUnc.Build("printsrv01", share));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankServer_FallsBackToMachineName(string? server)
    {
        var unc = SpoolerUnc.Build(server, "HR-MFP-01");
        Assert.Equal($@"\\{Environment.MachineName}\HR-MFP-01", unc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\")]
    [InlineData(@"\\")]
    public void BlankShare_Throws(string share)
    {
        Assert.Throws<ArgumentException>(() => SpoolerUnc.Build("printsrv01", share));
    }
}
