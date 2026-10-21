using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenPrintDeploy.Server.Drivers;
using OpenPrintDeploy.Server.Https;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

public sealed class DriverEndpointTests : IClassFixture<SyncEndpointTests.TestServerFactory>
{
    private readonly SyncEndpointTests.TestServerFactory _factory;
    public DriverEndpointTests(SyncEndpointTests.TestServerFactory factory) => _factory = factory;

    [Fact]
    public void TestHostDoesNotProvisionMachineCertificateOrRedirectHttp()
    {
        var https = _factory.Services.GetRequiredService<HttpsStatus>();
        Assert.False(https.Enabled);
        Assert.False(https.Enforced);
    }

    [Theory]
    [InlineData("/drivers/manifest")]
    [InlineData("/drivers/package")]
    public async Task DeliveryRejectsMissingOrForgedCapability(string endpoint)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        foreach (var ticket in new[] { "", "not-a-valid-ticket" })
        {
            var response = await client.PostAsJsonAsync(endpoint, new PackageRequest(ticket));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task DeliveryRejectsPlainHttpEvenWithValidTicket()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });
        var ticket = _factory.Services.GetRequiredService<DriverTickets>().Issue(@"\\printsrv01\HR-MFP-01");
        var response = await client.PostAsJsonAsync("/drivers/manifest", new PackageRequest(ticket));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryRejectsQueueNoLongerRegistered()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var ticket = _factory.Services.GetRequiredService<DriverTickets>().Issue(@"\\printsrv01\deleted-queue");
        var response = await client.PostAsJsonAsync("/drivers/manifest", new PackageRequest(ticket));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SyncIssuesCapabilitiesOnlyForAssignedQueues()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Dev-User", "hruser");
        var response = await client.PostAsJsonAsync("/sync", new SyncRequestDto(null));
        var result = await response.Content.ReadFromJsonAsync<SyncResponseDto>();
        Assert.NotNull(result);
        var tickets = _factory.Services.GetRequiredService<DriverTickets>();
        Assert.DoesNotContain(result.Printers, p => p.UncPath.Contains("ENG-Color"));
        if (OperatingSystem.IsWindows())
            Assert.All(result.Printers, p => Assert.Equal(p.UncPath, tickets.Read(p.DriverTicket!)));
        else Assert.All(result.Printers, p => Assert.Null(p.DriverTicket));
    }
}
