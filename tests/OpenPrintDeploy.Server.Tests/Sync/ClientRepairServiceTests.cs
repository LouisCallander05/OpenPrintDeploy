using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

public sealed class ClientRepairServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly ClientRepairService _repairs;
    private readonly ClaimsPrincipal _admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "DOMAIN\\admin")], "test"));
    public ClientRepairServiceTests()
    {
        _connection.Open();
        _factory = new(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _repairs = new(_factory, new Authorization(true));
    }

    private async Task<Guid> Enroll(bool capable = true, string? device = "DEVICE-A", string username = "alice")
    {
        var tracker = new ClientActivityTracker(_factory, NullLogger<ClientActivityTracker>.Instance);
        var sync = Guid.NewGuid();
        await tracker.RecordSyncStartedAsync(username, new("PC", sync, "test", device, capable), new([], SyncId: sync), default);
        await using var db = _factory.CreateDbContext();
        return await db.ClientUsers.Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    [Fact]
    public async Task RequiresAdministratorAndRepairCapableStableClient()
    {
        var user = await Enroll();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ClientRepairService(_factory, new Authorization(false)).RequestAsync(user, _admin));
        var unsupported = await Enroll(capable: false, device: "DEVICE-B", username: "bob");
        await Assert.ThrowsAsync<AdminValidationException>(() => _repairs.RequestAsync(unsupported, _admin));
        var legacy = await Enroll(device: null, username: "legacy");
        await Assert.ThrowsAsync<AdminValidationException>(() => _repairs.RequestAsync(legacy, _admin));
    }

    [Fact]
    public async Task RequestIsQueuedOnceAndDeliveredOnlyToItsDeviceUser()
    {
        var user = await Enroll();
        var id = await _repairs.RequestAsync(user, _admin);
        Assert.Equal(id, await _repairs.RequestAsync(user, _admin));
        Assert.Equal(id, await _repairs.GetPendingAsync("ALICE", "device-a", default));
        Assert.Null(await _repairs.GetPendingAsync("bob", "DEVICE-A", default));
        Assert.Null(await _repairs.GetPendingAsync("alice", "DEVICE-B", default));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.ClientActivities.CountAsync(a => a.Type == "Repair requested"));
    }

    [Fact]
    public async Task ReportsAreCorrelatedAndTerminalAcknowledgementsCannotRegress()
    {
        var user = await Enroll();
        var id = await _repairs.RequestAsync(user, _admin);
        Assert.False(await _repairs.ReportAsync("bob", new(id, "DEVICE-A", PrinterRepairStatus.Running), default));
        Assert.False(await _repairs.ReportAsync("alice", new(id, "DEVICE-B", PrinterRepairStatus.Running), default));
        Assert.True(await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Running), default));
        Assert.True(await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Completed), default));
        await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Completed), default);
        await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Running), default);
        Assert.Null(await _repairs.GetPendingAsync("alice", "DEVICE-A", default));
        await using var db = _factory.CreateDbContext();
        Assert.Equal("Completed", (await db.ClientUsers.SingleAsync()).RepairStatus);
        Assert.Equal(1, await db.ClientActivities.CountAsync(a => a.Type == "Repair completed"));
    }

    [Fact]
    public async Task FailedRequestRetryKeepsIdAndNewRequestAfterSuccessGetsNewId()
    {
        var user = await Enroll();
        var id = await _repairs.RequestAsync(user, _admin);
        await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Failed, "Printer busy"), default);
        Assert.Null(await _repairs.GetPendingAsync("alice", "DEVICE-A", default));
        Assert.Equal(id, await _repairs.RequestAsync(user, _admin));
        await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Completed), default);
        var next = await _repairs.RequestAsync(user, _admin);
        Assert.NotEqual(id, next);
        Assert.False(await _repairs.ReportAsync("alice", new(id, "DEVICE-A", PrinterRepairStatus.Completed), default));
        Assert.Equal(next, await _repairs.GetPendingAsync("alice", "DEVICE-A", default));
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
    }
    internal sealed class Authorization(bool allow) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
            => Task.FromResult(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}
