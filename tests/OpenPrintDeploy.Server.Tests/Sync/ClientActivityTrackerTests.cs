using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

public sealed class ClientActivityTrackerTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ClientActivityTracker _tracker;

    public ClientActivityTrackerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new SharedConnectionFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _tracker = new ClientActivityTracker(_factory, NullLogger<ClientActivityTracker>.Instance);
    }

    [Fact]
    public async Task StartAndReport_CreateDeviceUserAndSyncedPrinter()
    {
        var syncId = Guid.NewGuid();
        var printer = new PrinterDto("Library", @"\\printsrv\library");
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto("PC-001", syncId, "1.2.3"),
            new SyncResponseDto([printer], SyncId: syncId),
            default);

        await _tracker.RecordReportAsync(
            @"DOMAIN\alice",
            new SyncReportDto(
                syncId,
                "PC-001",
                "1.2.3",
                SyncReportStatus.Synced,
                [new PrinterSyncResultDto(
                    printer.DisplayName,
                    printer.UncPath,
                    PrinterSyncOperation.Installed,
                    Succeeded: true)]),
            default);

        await using var db = await _factory.CreateDbContextAsync();
        var device = await db.ClientDevices.Include(d => d.Users).SingleAsync();
        Assert.Equal("PC-001", device.MachineName);
        Assert.Equal("1.2.3", device.ClientVersion);
        var user = Assert.Single(device.Users);
        Assert.Equal(ClientSyncStatuses.Synced, user.LastSyncStatus);
        Assert.Equal(1, user.SyncedPrinterCount);
        var state = await db.ClientPrinters.SingleAsync();
        Assert.Equal(ClientPrinterStatuses.Present, state.Status);
        Assert.Equal("Installed", state.LastOperation);
    }

    [Fact]
    public async Task SuccessfulRemoval_LeavesHistoryAndRemovesCurrentState()
    {
        var firstSync = Guid.NewGuid();
        var printer = new PrinterDto("Old printer", @"\\printsrv\old");
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto("PC-001", firstSync),
            new SyncResponseDto([printer], SyncId: firstSync),
            default);
        await _tracker.RecordReportAsync(
            @"DOMAIN\alice",
            new SyncReportDto(firstSync, "PC-001", null, SyncReportStatus.Synced,
                [new PrinterSyncResultDto(printer.DisplayName, printer.UncPath, PrinterSyncOperation.Present, true)]),
            default);

        var removalSync = Guid.NewGuid();
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto("PC-001", removalSync),
            new SyncResponseDto([], SyncId: removalSync),
            default);
        await _tracker.RecordReportAsync(
            @"DOMAIN\alice",
            new SyncReportDto(removalSync, "PC-001", null, SyncReportStatus.Synced,
                [new PrinterSyncResultDto(
                    null,
                    printer.UncPath,
                    PrinterSyncOperation.Removed,
                    true,
                    RemovalReason: PrinterRemovalReason.NoLongerAssigned)]),
            default);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ClientPrinters.ToListAsync());
        var removal = await db.ClientActivities.SingleAsync(a => a.Type == "Printer removed");
        Assert.Contains("no longer assigned", removal.Summary);
        Assert.Equal(printer.UncPath, removal.PrinterUncPath);
    }

    [Fact]
    public async Task DuplicateReport_IsIdempotent()
    {
        var syncId = Guid.NewGuid();
        var request = new SyncRequestDto("PC-001", syncId);
        var response = new SyncResponseDto([], SyncId: syncId);
        var report = new SyncReportDto(syncId, "PC-001", null, SyncReportStatus.Synced, []);

        await _tracker.RecordSyncStartedAsync(@"DOMAIN\alice", request, response, default);
        await _tracker.RecordReportAsync(@"DOMAIN\alice", report, default);
        await _tracker.RecordReportAsync(@"DOMAIN\alice", report, default);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.ClientActivities.CountAsync(a => a.Type == "Client seen"));
    }

    [Fact]
    public async Task StableDeviceIds_DoNotMergeWithCollidingLegacyName()
    {
        const string legacyName = "3931-STU-S-PW0B";
        const string firstFullName = "3931-STU-S-PW0B-LIBRARY";
        const string secondFullName = "3931-STU-S-PW0B-SCIENCE";
        var firstSync = Guid.NewGuid();

        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto(legacyName, firstSync, "0.9.16"),
            new SyncResponseDto([], SyncId: firstSync),
            default);

        var firstDeviceSync = Guid.NewGuid();
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto(firstFullName, firstDeviceSync, "0.9.17", "DEVICE-A"),
            new SyncResponseDto([], SyncId: firstDeviceSync),
            default);

        var secondDeviceSync = Guid.NewGuid();
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\bob",
            new SyncRequestDto(secondFullName, secondDeviceSync, "0.9.17", "DEVICE-B"),
            new SyncResponseDto([], SyncId: secondDeviceSync),
            default);

        await using var db = await _factory.CreateDbContextAsync();
        var devices = await db.ClientDevices.OrderBy(d => d.MachineName).ToListAsync();
        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, d => d.DeviceIdentifier is null && d.MachineName == legacyName);
        Assert.Contains(devices, d => d.DeviceIdentifier == "DEVICE-A" && d.MachineName == firstFullName);
        Assert.Contains(devices, d => d.DeviceIdentifier == "DEVICE-B" && d.MachineName == secondFullName);
    }

    [Fact]
    public async Task StableDeviceId_PreservesRecordWhenHostnameChanges()
    {
        const string deviceId = "DEVICE-A";
        var firstSync = Guid.NewGuid();
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto("OLD-LONG-HOSTNAME", firstSync, "0.9.17", deviceId),
            new SyncResponseDto([], SyncId: firstSync),
            default);

        var secondSync = Guid.NewGuid();
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto("NEW-LONG-HOSTNAME", secondSync, "0.9.17", deviceId),
            new SyncResponseDto([], SyncId: secondSync),
            default);

        await using var db = await _factory.CreateDbContextAsync();
        var device = await db.ClientDevices.Include(d => d.Users).SingleAsync();
        Assert.Equal("NEW-LONG-HOSTNAME", device.MachineName);
        Assert.Equal(deviceId, device.DeviceIdentifier);
        Assert.Single(device.Users);
        Assert.Equal(1, await db.ClientActivities.CountAsync(a => a.Type == "Client seen"));
    }

    [Fact]
    public async Task AdminQueries_ReturnDeviceAndDetails()
    {
        var syncId = Guid.NewGuid();
        await _tracker.RecordSyncStartedAsync(
            @"DOMAIN\alice",
            new SyncRequestDto("PC-001", syncId, "1.2.3"),
            new SyncResponseDto([], SyncId: syncId),
            default);
        await _tracker.RecordReportAsync(
            @"DOMAIN\alice",
            new SyncReportDto(syncId, "PC-001", "1.2.3", SyncReportStatus.Synced, []),
            default);

        var admin = new ClientActivityAdminService(_factory);
        var summary = Assert.Single(await admin.GetDevicesAsync("alice"));
        var details = await admin.GetDeviceAsync(summary.Id);

        Assert.NotNull(details);
        Assert.Equal("PC-001", details!.MachineName);
        Assert.Equal(@"DOMAIN\alice", Assert.Single(details.Users).Username);
        Assert.NotEmpty(details.Activities);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class SharedConnectionFactory : IDbContextFactory<AppDbContext>
    {
        private readonly SqliteConnection _connection;
        public SharedConnectionFactory(SqliteConnection connection) => _connection = connection;

        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
    }
}
