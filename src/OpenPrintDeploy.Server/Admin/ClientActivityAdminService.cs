using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;

namespace OpenPrintDeploy.Server.Admin;

public sealed class ClientActivityAdminService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ClientActivityAdminService(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<ClientDeviceSummary>> GetDevicesAsync(
        string? search = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var normalizedSearch = search?.Trim().ToUpperInvariant();
        var query = db.ClientDevices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(d =>
                d.NormalizedMachineName.Contains(normalizedSearch)
                || d.Users.Any(u => u.NormalizedUsername.Contains(normalizedSearch))
                || d.Users.Any(u => u.Printers.Any(p =>
                    p.NormalizedUncPath.Contains(normalizedSearch)
                    || (p.DisplayName != null && p.DisplayName.ToUpper().Contains(normalizedSearch)))));
        }

        var devices = await query
            .Select(d => new
            {
                d.Id,
                d.MachineName,
                d.ClientVersion,
                d.FirstSeenAt,
                d.LastSeenAt,
            })
            .ToListAsync(ct);
        var ids = devices.Select(d => d.Id).ToList();
        var users = await db.ClientUsers
            .AsNoTracking()
            .Where(u => ids.Contains(u.DeviceId))
            .Select(u => new
            {
                u.DeviceId,
                u.Username,
                u.LastSeenAt,
                u.LastSyncStatus,
                u.LastSyncStartedAt,
                u.FailedPrinterCount,
                PrinterCount = u.Printers.Count,
            })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return devices
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d =>
            {
                var deviceUsers = users.Where(u => u.DeviceId == d.Id).ToList();
                var hasAttention = deviceUsers.Any(u => u.LastSyncStatus is "Failed" or "Partial");
                var hasActiveSync = deviceUsers.Any(u =>
                    u.LastSyncStatus == "Syncing"
                    && u.LastSyncStartedAt is { } started
                    && now - started <= TimeSpan.FromMinutes(2));
                var hasWarning = deviceUsers.Any(u =>
                    u.LastSyncStatus is "Deferred" or "Unreported" or "Syncing");
                var health = hasAttention ? "Attention"
                    : hasActiveSync ? "Syncing"
                    : hasWarning ? "Warning"
                    : "Healthy";
                return new ClientDeviceSummary(
                    d.Id,
                    d.MachineName,
                    d.ClientVersion,
                    d.FirstSeenAt,
                    d.LastSeenAt,
                    deviceUsers.Count,
                    deviceUsers.OrderByDescending(u => u.LastSeenAt).Select(u => u.Username).FirstOrDefault(),
                    deviceUsers.Sum(u => u.PrinterCount),
                    deviceUsers.Sum(u => u.FailedPrinterCount),
                    health);
            })
            .ToList();
    }

    public async Task<ClientDeviceDetails?> GetDeviceAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var device = await db.ClientDevices
            .AsNoTracking()
            .AsSplitQuery()
            .Include(d => d.Users)
                .ThenInclude(u => u.Printers)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return null;
        }

        var activityRows = await db.ClientActivities
            .AsNoTracking()
            .Where(a => a.ClientUser.DeviceId == id)
            .Select(a => new ClientActivityDetails(
                a.OccurredAt,
                a.ClientUser.Username,
                a.Type,
                a.Summary,
                a.PrinterDisplayName,
                a.PrinterUncPath,
                a.Error))
            .ToListAsync(ct);
        var activities = activityRows
            .OrderByDescending(a => a.OccurredAt)
            .Take(100)
            .ToList();

        var users = device.Users
            .OrderByDescending(u => u.LastSeenAt)
            .Select(u => new ClientUserDetails(
                u.Id,
                u.Username,
                u.FirstSeenAt,
                u.LastSeenAt,
                u.LastSyncStartedAt,
                u.LastSyncCompletedAt,
                u.LastSyncStatus,
                u.AssignedPrinterCount,
                u.SyncedPrinterCount,
                u.FailedPrinterCount,
                u.LastError,
                u.Printers
                    .OrderBy(p => p.DisplayName ?? p.UncPath)
                    .Select(p => new ClientPrinterDetails(
                        p.DisplayName,
                        p.UncPath,
                        p.Status,
                        p.LastOperation,
                        p.UpdatedAt,
                        p.LastError))
                    .ToList()))
            .ToList();

        return new ClientDeviceDetails(
            device.Id,
            device.MachineName,
            device.ClientVersion,
            device.FirstSeenAt,
            device.LastSeenAt,
            users,
            activities);
    }
}

public sealed record ClientDeviceSummary(
    Guid Id,
    string MachineName,
    string? ClientVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int UserCount,
    string? LastUsername,
    int PrinterCount,
    int FailedPrinterCount,
    string Health);

public sealed record ClientDeviceDetails(
    Guid Id,
    string MachineName,
    string? ClientVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<ClientUserDetails> Users,
    IReadOnlyList<ClientActivityDetails> Activities);

public sealed record ClientUserDetails(
    Guid Id,
    string Username,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastSyncStartedAt,
    DateTimeOffset? LastSyncCompletedAt,
    string Status,
    int AssignedPrinterCount,
    int SyncedPrinterCount,
    int FailedPrinterCount,
    string? LastError,
    IReadOnlyList<ClientPrinterDetails> Printers);

public sealed record ClientPrinterDetails(
    string? DisplayName,
    string UncPath,
    string Status,
    string? LastOperation,
    DateTimeOffset UpdatedAt,
    string? LastError);

public sealed record ClientActivityDetails(
    DateTimeOffset OccurredAt,
    string Username,
    string Type,
    string Summary,
    string? PrinterDisplayName,
    string? PrinterUncPath,
    string? Error);
