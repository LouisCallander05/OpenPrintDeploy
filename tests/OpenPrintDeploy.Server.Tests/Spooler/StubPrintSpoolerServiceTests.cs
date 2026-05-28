using OpenPrintDeploy.Server.Spooler;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Spooler;

public sealed class StubPrintSpoolerServiceTests
{
    [Fact]
    public async Task ReturnsEmpty()
    {
        var svc = new StubPrintSpoolerService();
        Assert.Empty(await svc.GetSharedPrintersAsync());
    }
}
