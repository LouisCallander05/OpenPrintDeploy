using System.Runtime.Versioning;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

[SupportedOSPlatform("windows")]
public sealed class ServerQueueDriverTests
{
    private static readonly InstalledDriver Server = new("Lexmark Universal v4 XL", "Windows x64", 4, 123, "lexmark.inf");
    private static readonly PreparedDriver Prepared = new(Server.Name, Server.Environment, Server.Version, Server.Date);

    [Fact]
    public void Type4ShareResolvesActualServerDriverInsteadOfClientRenderingProjection()
    {
        var projected = new InstalledDriver("Microsoft enhanced Point and Print driver", "Windows x64", 10, 100, "printqueue.inf");
        var queried = false;
        var actual = WindowsServerQueueDriver.GetCore(@"\\server\Staffroom Printer", server =>
        {
            Assert.Equal(@"\\server", server);
            return [("Staffroom Printer", Server.Name), ("Other queue", projected.Name)];
        }, (name, server) =>
        {
            queried = true;
            Assert.Equal(@"\\server", server);
            Assert.Equal(Server.Name, name);
            return Server;
        });
        Assert.True(queried);
        PrinterDriverIdentity.Verify(actual, Prepared, "live server queue");
        Assert.Throws<InvalidOperationException>(() => PrinterDriverIdentity.Verify(projected, Prepared, "client projection"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("environment")]
    [InlineData("version")]
    [InlineData("date")]
    [InlineData("missing")]
    public void RealPreparedOrServerIdentityMismatchStillBlocksRepair(string difference)
    {
        var actual = difference switch
        {
            "name" => Server with { Name = "Different driver" },
            "environment" => Server with { Environment = "Windows ARM64" },
            "version" => Server with { Version = 5 },
            "date" => Server with { Date = 124 },
            _ => null
        };
        var error = Assert.Throws<InvalidOperationException>(() => PrinterDriverIdentity.Verify(actual, Prepared, "live server queue"));
        Assert.Contains(Server.Name, error.Message);
        Assert.Contains("live server queue", error.Message);
        Assert.Contains("found", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAmbiguousShareCannotSelectAnUnrelatedInstalledDriver(bool duplicate)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsServerQueueDriver.GetCore(@"\\server\Staffroom Printer",
            _ => duplicate ? [("Staffroom Printer", Server.Name), ("STAFFROOM PRINTER", Server.Name)] : [("Other", Server.Name)],
            (_, _) => throw new Exception("Driver lookup must not occur for an unverified share.")));
    }

    [Fact]
    public void ShareDriverChangeCannotBeHiddenByAnOldPreparedDriverStillInstalledOnServer()
    {
        var changed = Server with { Name = "Replacement driver" };
        var actual = WindowsServerQueueDriver.GetCore(@"\\server\Staffroom Printer",
            _ => [("Staffroom Printer", changed.Name)], (name, _) => name == changed.Name ? changed : Server);
        Assert.Throws<InvalidOperationException>(() => PrinterDriverIdentity.Verify(actual, Prepared, "live server queue"));
    }

    [Theory]
    [InlineData(@"server\queue")]
    [InlineData(@"\\server\queue\file")]
    [InlineData(@"\\server\..")]
    [InlineData(@"\\server\queue:stream")]
    public void InvalidSharedPathsAreRejectedBeforeRemoteLookup(string unc)
        => Assert.Throws<InvalidDataException>(() => WindowsServerQueueDriver.GetCore(unc,
            _ => throw new Exception("No remote query expected"), (_, _) => Server));
}
