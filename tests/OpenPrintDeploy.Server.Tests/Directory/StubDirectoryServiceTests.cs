using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Directory;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Directory;

public sealed class StubDirectoryServiceTests
{
    private static StubDirectoryService Create()
    {
        var options = new DirectoryOptions();
        options.Stub.Users["hruser"] = ["S-1-5-21-DEMO-HR"];
        options.Stub.Users["bothuser"] = ["S-1-5-21-DEMO-HR", "S-1-5-21-DEMO-ENG"];
        options.Stub.Machines["PC-1"] = "OU=Sales,DC=corp,DC=local";
        return new StubDirectoryService(Options.Create(options));
    }

    [Fact]
    public async Task ResolvesConfiguredUser()
    {
        var sids = await Create().GetGroupSidsAsync("hruser");
        Assert.Equal(["S-1-5-21-DEMO-HR"], sids);
    }

    [Fact]
    public async Task NormalizesDomainQualifiedAndUpnNames()
    {
        var svc = Create();
        Assert.Contains("S-1-5-21-DEMO-HR", await svc.GetGroupSidsAsync(@"CORP\hruser"));
        Assert.Contains("S-1-5-21-DEMO-HR", await svc.GetGroupSidsAsync("hruser@corp.local"));
    }

    [Fact]
    public async Task UnknownUser_ReturnsEmpty()
    {
        Assert.Empty(await Create().GetGroupSidsAsync("nobody"));
    }

    [Fact]
    public async Task ResolvesMachineOu_AndNullForUnknown()
    {
        var svc = Create();
        Assert.Equal("OU=Sales,DC=corp,DC=local", await svc.GetMachineOuDnAsync("PC-1"));
        Assert.Null(await svc.GetMachineOuDnAsync("PC-X"));
    }
}
