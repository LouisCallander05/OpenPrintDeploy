using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
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

    [Fact]
    public async Task SyncReport_PersistsClientAndPrinterOutcome()
    {
        var client = _factory.CreateClient();
        var syncId = Guid.NewGuid();
        var machine = $"REPORT-{Guid.NewGuid():N}";
        var syncRequest = new HttpRequestMessage(HttpMethod.Post, "/sync")
        {
            Content = JsonContent.Create(new SyncRequestDto(machine, syncId, "1.2.3")),
        };
        syncRequest.Headers.Add("X-Dev-User", "hruser");
        var syncResponse = await client.SendAsync(syncRequest);
        syncResponse.EnsureSuccessStatusCode();
        var assignment = await syncResponse.Content.ReadFromJsonAsync<SyncResponseDto>();
        Assert.NotNull(assignment);

        var results = assignment!.Printers
            .Select(p => new PrinterSyncResultDto(
                p.DisplayName, p.UncPath, PrinterSyncOperation.Present, Succeeded: true))
            .ToList();
        var reportRequest = new HttpRequestMessage(HttpMethod.Post, "/sync/report")
        {
            Content = JsonContent.Create(new SyncReportDto(
                syncId, machine, "1.2.3", SyncReportStatus.Synced, results)),
        };
        reportRequest.Headers.Add("X-Dev-User", "hruser");
        var reportResponse = await client.SendAsync(reportRequest);

        Assert.Equal(HttpStatusCode.NoContent, reportResponse.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.ClientDevices.SingleAsync(d => d.MachineName == machine);
        var user = await db.ClientUsers.SingleAsync(u => u.DeviceId == device.Id);
        Assert.Equal("Synced", user.LastSyncStatus);
        Assert.Equal(results.Count, await db.ClientPrinters.CountAsync(p => p.ClientUserId == user.Id));
    }

    public sealed class TestServerFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"opd-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // WindowsServiceLifetime registers Event Log logging on Windows;
            // TestServer must not require permission to write the machine log.
            builder.ConfigureLogging(logging => logging.ClearProviders());
            // UseEnvironment("Development") drives both the dev auth handler and
            // the Stub directory (the production code keys those choices off
            // IHostEnvironment, not config). What we still need to set is the
            // dev-handler default user, the Stub user→SID map matching the
            // seeded zones, and an isolated SQLite file for hermetic tests.
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AppDb"] = $"Data Source={_dbPath}",
                    ["Auth:Dev:DefaultUser"] = "hruser",
                    ["Directory:Stub:Users:hruser:0"] = "S-1-5-21-DEMO-HR",
                    ["Directory:Stub:Users:enguser:0"] = "S-1-5-21-DEMO-ENG",
                    // Keep the in-process TestServer on plain HTTP. Without this, an
                    // admin-rights host (e.g. a Windows CI runner) would provision a
                    // cert, RequireHttps would turn on, and these HTTP requests would
                    // get 307-redirected. In-memory config wins over appsettings.
                    ["Https:Enabled"] = "false",
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
