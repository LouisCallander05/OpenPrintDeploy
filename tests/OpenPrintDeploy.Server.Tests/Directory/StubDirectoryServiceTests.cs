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
        options.Stub.Groups["HR-Staff"] = "S-1-5-21-DEMO-HR";
        options.Stub.Groups["Engineering-Team"] = "S-1-5-21-DEMO-ENG";
        options.Stub.Groups["Lobby-Visitors"] = "S-1-5-21-DEMO-LOBBY";
        return new StubDirectoryService(Options.Create(options));
    }

    [Fact]
    public async Task ResolvesConfiguredUser()
    {
        var resolution = await Create().GetGroupSidsAsync("hruser");
        Assert.Equal(["S-1-5-21-DEMO-HR"], resolution.Sids);
        Assert.True(resolution.Available);
    }

    [Fact]
    public async Task NormalizesDomainQualifiedAndUpnNames()
    {
        var svc = Create();
        Assert.Contains("S-1-5-21-DEMO-HR", (await svc.GetGroupSidsAsync(@"CORP\hruser")).Sids);
        Assert.Contains("S-1-5-21-DEMO-HR", (await svc.GetGroupSidsAsync("hruser@corp.local")).Sids);
    }

    [Fact]
    public async Task UnknownUser_ReturnsEmptyButAvailable()
    {
        var resolution = await Create().GetGroupSidsAsync("nobody");
        Assert.Empty(resolution.Sids);
        Assert.True(resolution.Available);
    }

    [Fact]
    public async Task SearchGroups_EmptyQuery_ReturnsAllAlphabetical()
    {
        var groups = await Create().SearchGroupsAsync(string.Empty, limit: 10);
        Assert.Equal(
            ["Engineering-Team", "HR-Staff", "Lobby-Visitors"],
            groups.Select(g => g.Name));
    }

    [Fact]
    public async Task SearchGroups_FiltersBySubstringCaseInsensitively()
    {
        var groups = await Create().SearchGroupsAsync("staff", limit: 10);
        Assert.Equal(["HR-Staff"], groups.Select(g => g.Name));
    }

    [Fact]
    public async Task SearchGroups_RespectsLimit()
    {
        var groups = await Create().SearchGroupsAsync(string.Empty, limit: 2);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task ResolveGroupName_ReturnsConfiguredName()
    {
        Assert.Equal("HR-Staff", await Create().ResolveGroupNameAsync("S-1-5-21-DEMO-HR"));
    }

    [Fact]
    public async Task ResolveGroupName_UnknownSid_ReturnsNull()
    {
        Assert.Null(await Create().ResolveGroupNameAsync("S-1-5-21-NOT-CONFIGURED"));
    }

    [Fact]
    public async Task Diagnostics_ReportsStubProviderAndConnected()
    {
        var diag = await Create().GetDiagnosticsAsync();

        Assert.Equal("Stub", diag.Provider);
        Assert.True(diag.Connected);
        Assert.Equal(3, diag.SampleGroupCount);
        Assert.Null(diag.Error);
    }
}
