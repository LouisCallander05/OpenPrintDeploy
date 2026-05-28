using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

/// <summary>
/// Drives the real <c>/sync</c> endpoint via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// in Development (Dev auth + Stub directory + seeded demo data), against an
/// isolated temp SQLite file so the repo's database is never touched.
/// </summary>
public sealed class SyncEndpointTests : IClassFixture<SyncEndpointTests.TestServerFactory>
{
    private readonly TestServerFactory _factory;

    public SyncEndpointTests(TestServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Sync_AsHrUser_ReturnsHrPrinters()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync")
        {
            Content = JsonContent.Create(new SyncRequestDto(MachineName: null)),
        };
        request.Headers.Add("X-Dev-User", "hruser");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SyncResponseDto>();
        Assert.NotNull(dto);
        Assert.Contains(dto!.Printers, p => p.UncPath == @"\\printsrv01\HR-MFP-01");
        Assert.Contains(dto.Printers, p => p.UncPath == @"\\printsrv01\Lobby-Mono");
        Assert.DoesNotContain(dto.Printers, p => p.UncPath == @"\\printsrv01\ENG-Color-01");
    }

    [Fact]
    public async Task Sync_AsUnknownUser_ReturnsEmptySet()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync")
        {
            Content = JsonContent.Create(new SyncRequestDto(MachineName: null)),
        };
        request.Headers.Add("X-Dev-User", "nobody");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SyncResponseDto>();
        Assert.NotNull(dto);
        Assert.Empty(dto!.Printers);
    }

    [Fact]
    public async Task Sync_WithoutAnyIdentity_Returns401()
    {
        // Clear the dev default user so an un-headered request is anonymous.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Dev:DefaultUser"] = "",
                })));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/sync", new SyncRequestDto(MachineName: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class TestServerFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"opd-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // Tests must own their config rather than depend on a developer's
            // (gitignored) appsettings.Development.json. Pin the dev auth handler,
            // the in-memory Stub directory mapped to the seeded zone SIDs, and an
            // isolated SQLite file so the run is hermetic on any machine.
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AppDb"] = $"Data Source={_dbPath}",
                    ["Auth:Mode"] = "Dev",
                    ["Auth:Dev:DefaultUser"] = "hruser",
                    ["Directory:Provider"] = "Stub",
                    ["Directory:Stub:Users:hruser:0"] = "S-1-5-21-DEMO-HR",
                    ["Directory:Stub:Users:enguser:0"] = "S-1-5-21-DEMO-ENG",
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing)
            {
                return;
            }

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(_dbPath + suffix);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the throwaway test database.
                }
            }
        }
    }
}
