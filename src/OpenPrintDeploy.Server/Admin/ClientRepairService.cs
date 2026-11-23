using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Auth;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Server.Admin;

public sealed class ClientRepairService(IDbContextFactory<AppDbContext> factory, IAuthorizationService authorization)
{
    public async Task<Guid> RequestAsync(Guid clientUserId, ClaimsPrincipal admin, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(admin, null, AuthPolicies.Admin)).Succeeded)
            throw new UnauthorizedAccessException("Administrator access is required to request a printer repair.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await db.ClientUsers.Include(u => u.Device).SingleOrDefaultAsync(u => u.Id == clientUserId, ct)
            ?? throw new AdminValidationException("That client user no longer exists.");
        if (!user.SupportsPrinterRepair || string.IsNullOrWhiteSpace(user.Device.DeviceIdentifier))
            throw new AdminValidationException("Update this user's client and let it sync before requesting a repair.");
        if (user.RepairStatus is "Pending" or "Running") return user.RepairRequestId!.Value;

        // Retrying a failed request keeps its ID so the client skips queues it
        // already repaired. A new request after completion gets a new ID.
        var requestId = user.RepairStatus == "Failed" && user.RepairRequestId is { } failed ? failed : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var requestedBy = admin.Identity?.Name ?? "Administrator";
        requestedBy = requestedBy[..Math.Min(requestedBy.Length, 256)];
        var updated = await db.ClientUsers.Where(u => u.Id == clientUserId &&
                (u.RepairStatus == null || (u.RepairStatus != "Pending" && u.RepairStatus != "Running")))
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.RepairRequestId, (Guid?)requestId)
                .SetProperty(u => u.RepairRequestedAt, (DateTimeOffset?)now)
                .SetProperty(u => u.RepairRequestedBy, requestedBy)
                .SetProperty(u => u.RepairStartedAt, (DateTimeOffset?)null)
                .SetProperty(u => u.RepairCompletedAt, (DateTimeOffset?)null)
                .SetProperty(u => u.RepairStatus, "Pending")
                .SetProperty(u => u.RepairError, (string?)null), ct);
        if (updated == 0) throw new AdminValidationException("A repair is already pending. Refresh this page.");
        db.ClientActivities.Add(new ClientActivityEntity
        {
            ClientUser = user, Type = "Repair requested", OccurredAt = now,
            Summary = $"{requestedBy} requested a repair of OPD-managed printer connections."
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return requestId;
    }

    public async Task<Guid?> GetPendingAsync(string username, string? deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var userName = username.Trim().ToUpperInvariant();
        var device = deviceId.Trim().ToUpperInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ClientUsers.Where(u => u.NormalizedUsername == userName && u.Device.DeviceIdentifier == device
                && u.SupportsPrinterRepair && (u.RepairStatus == "Pending" || u.RepairStatus == "Running"))
            .Select(u => u.RepairRequestId).SingleOrDefaultAsync(ct);
    }

    public async Task<bool> ReportAsync(string username, PrinterRepairReportDto report, CancellationToken ct)
    {
        if (report.Status == PrinterRepairStatus.Pending) return false;
        var userName = username.Trim().ToUpperInvariant();
        var device = report.DeviceId.Trim().ToUpperInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await db.ClientUsers.SingleOrDefaultAsync(u => u.NormalizedUsername == userName
            && u.Device.DeviceIdentifier == device && u.RepairRequestId == report.RequestId, ct);
        if (user is null) return false;
        // Terminal reports and late/replayed acknowledgements are idempotent.
        if (user.RepairStatus is "Completed" or "Failed") return true;
        var status = report.Status.ToString();
        if (user.RepairStatus == status && user.RepairError == report.Error) return true;
        var now = DateTimeOffset.UtcNow;
        var previous = user.RepairStatus;
        user.RepairStatus = status;
        user.RepairStartedAt ??= now;
        user.RepairError = report.Error;
        if (report.Status is PrinterRepairStatus.Completed or PrinterRepairStatus.Failed) user.RepairCompletedAt = now;
        if (previous != status)
            db.ClientActivities.Add(new ClientActivityEntity
            {
                ClientUser = user, Type = "Repair " + status.ToLowerInvariant(), OccurredAt = now,
                Summary = $"Printer connection repair {status.ToLowerInvariant()}.", Error = report.Error
            });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
