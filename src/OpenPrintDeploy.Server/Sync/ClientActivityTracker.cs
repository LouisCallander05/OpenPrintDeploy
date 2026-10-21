using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Server.Sync;

/// <summary>
/// Best-effort persistence for the operational client view. A tracking failure
/// is logged but never allowed to interrupt assignment delivery.
/// </summary>
public sealed class ClientActivityTracker
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<ClientActivityTracker> _logger;

    public ClientActivityTracker(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<ClientActivityTracker> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task RecordSyncStartedAsync(
        string username,
        SyncRequestDto request,
        SyncResponseDto response,
        CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var (device, clientUser, isNewUser) = await GetOrCreateAsync(
                db,
                request.MachineName,
                request.DeviceId,
                username,
                request.ClientVersion,
                now,
                ct);

            var priorVersion = device.ClientVersion;
            device.LastSeenAt = now;
            if (!string.IsNullOrWhiteSpace(request.ClientVersion))
            {
                device.ClientVersion = Limit(request.ClientVersion.Trim(), 64);
            }

            clientUser.LastSeenAt = now;
            clientUser.LastSyncId = response.SyncId;
            clientUser.LastSyncStartedAt = now;
            clientUser.LastSyncCompletedAt = null;
            clientUser.LastSyncStatus = response.Authoritative
                ? ClientSyncStatuses.Syncing
                : ClientSyncStatuses.Deferred;
            clientUser.AssignedPrinterCount = response.Printers.Count;
            clientUser.LastError = null;

            if (isNewUser)
            {
                AddActivity(clientUser, "Client seen", $"{clientUser.Username} first synced from {device.MachineName}.", response.SyncId, now);
            }
            else if (!string.Equals(priorVersion, device.ClientVersion, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(priorVersion))
            {
                AddActivity(clientUser, "Version changed", $"Client updated from {priorVersion} to {device.ClientVersion}.", response.SyncId, now);
            }

            var desired = response.Printers
                .ToDictionary(p => Normalize(p.UncPath, 260), StringComparer.Ordinal);
            var existing = await db.ClientPrinters
                .Where(p => p.ClientUserId == clientUser.Id)
                .ToListAsync(ct);

            foreach (var printer in response.Printers)
            {
                var normalizedUnc = Normalize(printer.UncPath, 260);
                var state = existing.FirstOrDefault(p => p.NormalizedUncPath == normalizedUnc);
                if (state is null)
                {
                    state = new ClientPrinterEntity
                    {
                        ClientUser = clientUser,
                        DisplayName = Limit(printer.DisplayName, 128),
                        UncPath = Limit(printer.UncPath, 260)!,
                        NormalizedUncPath = normalizedUnc,
                        Status = ClientPrinterStatuses.Pending,
                        UpdatedAt = now,
                    };
                    db.ClientPrinters.Add(state);
                    AddActivity(
                        clientUser,
                        "Printer assigned",
                        $"{printer.DisplayName} was assigned.",
                        response.SyncId,
                        now,
                        printer.DisplayName,
                        printer.UncPath);
                }
                else
                {
                    state.DisplayName = Limit(printer.DisplayName, 128);
                    state.UncPath = Limit(printer.UncPath, 260)!;
                }
            }

            if (response.Authoritative)
            {
                foreach (var state in existing.Where(p => !desired.ContainsKey(p.NormalizedUncPath)))
                {
                    if (state.Status != ClientPrinterStatuses.RemovalPending)
                    {
                        state.Status = ClientPrinterStatuses.RemovalPending;
                        state.LastOperation = "Remove";
                        state.LastError = null;
                        state.UpdatedAt = now;
                    }
                }
            }

            foreach (var unc in response.RemovePrinters ?? [])
            {
                var normalizedUnc = Normalize(unc, 260);
                var state = existing.FirstOrDefault(p => p.NormalizedUncPath == normalizedUnc);
                if (state is null && !desired.ContainsKey(normalizedUnc))
                {
                    db.ClientPrinters.Add(new ClientPrinterEntity
                    {
                        ClientUser = clientUser,
                        UncPath = Limit(unc, 260)!,
                        NormalizedUncPath = normalizedUnc,
                        Status = ClientPrinterStatuses.RemovalPending,
                        LastOperation = "Remove",
                        UpdatedAt = now,
                    });
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record sync start for {User} on {Machine}.", username, request.MachineName);
        }
    }

    public async Task RecordReportAsync(string username, SyncReportDto report, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var (device, clientUser, _) = await GetOrCreateAsync(
                db,
                report.MachineName,
                report.DeviceId,
                username,
                report.ClientVersion,
                now,
                ct);

            // A late report from an older overlapping sync must not replace the
            // current state. A repeated report for an already-completed sync is
            // idempotent and needs no additional activity entries.
            if (clientUser.LastSyncId is { } current && current != report.SyncId)
            {
                return;
            }
            if (clientUser.LastSyncId == report.SyncId && clientUser.LastSyncCompletedAt is not null)
            {
                return;
            }

            device.LastSeenAt = now;
            if (!string.IsNullOrWhiteSpace(report.ClientVersion))
            {
                device.ClientVersion = Limit(report.ClientVersion.Trim(), 64);
            }

            var previousStatus = clientUser.LastSyncStatus;
            var previousError = clientUser.LastError;
            clientUser.LastSeenAt = now;
            clientUser.LastSyncId = report.SyncId;
            clientUser.LastSyncCompletedAt = now;
            clientUser.LastSyncStatus = StatusName(report.Status);
            clientUser.LastError = Limit(report.Error, 1024);
            clientUser.SyncedPrinterCount = report.Printers.Count(p => p.Operation != PrinterSyncOperation.Removed && p.Succeeded);
            clientUser.FailedPrinterCount = report.Printers.Count(p => !p.Succeeded);

            var states = await db.ClientPrinters
                .Where(p => p.ClientUserId == clientUser.Id)
                .ToListAsync(ct);

            foreach (var result in report.Printers)
            {
                ApplyPrinterResult(db, clientUser, states, result, report.SyncId, now);
            }

            if (report.Status == SyncReportStatus.Failed
                && (previousStatus != ClientSyncStatuses.Failed
                    || !string.Equals(previousError, clientUser.LastError, StringComparison.Ordinal)))
            {
                AddActivity(
                    clientUser,
                    "Sync failed",
                    "The client could not complete its sync.",
                    report.SyncId,
                    now,
                    error: clientUser.LastError);
            }
            else if (previousStatus is ClientSyncStatuses.Failed or ClientSyncStatuses.Partial
                     && report.Status == SyncReportStatus.Synced)
            {
                AddActivity(clientUser, "Sync recovered", "The client returned to a healthy synced state.", report.SyncId, now);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not record sync report for {User} on {Machine}.", username, report.MachineName);
        }
    }

    private static void ApplyPrinterResult(
        AppDbContext db,
        ClientUserEntity clientUser,
        List<ClientPrinterEntity> states,
        PrinterSyncResultDto result,
        Guid syncId,
        DateTimeOffset now)
    {
        var normalizedUnc = Normalize(result.UncPath, 260);
        var state = states.FirstOrDefault(p => p.NormalizedUncPath == normalizedUnc);

        if (result.Operation == PrinterSyncOperation.Removed && result.Succeeded)
        {
            var displayName = result.DisplayName ?? state?.DisplayName;
            var reason = result.RemovalReason == PrinterRemovalReason.GlobalRemoval
                ? "an administrator required its removal"
                : "it was no longer assigned";
            var resultText = result.AlreadyAbsent ? "was already absent" : "was removed";
            AddActivity(
                clientUser,
                "Printer removed",
                $"{displayName ?? result.UncPath} {resultText} because {reason}.",
                syncId,
                now,
                displayName,
                result.UncPath);
            if (state is not null)
            {
                db.ClientPrinters.Remove(state);
                states.Remove(state);
            }
            return;
        }

        if (state is null)
        {
            state = new ClientPrinterEntity
            {
                ClientUser = clientUser,
                DisplayName = Limit(result.DisplayName, 128),
                UncPath = Limit(result.UncPath, 260)!,
                NormalizedUncPath = normalizedUnc,
            };
            db.ClientPrinters.Add(state);
            states.Add(state);
        }

        var priorStatus = state.Status;
        var priorError = state.LastError;
        state.DisplayName = Limit(result.DisplayName, 128) ?? state.DisplayName;
        state.UncPath = Limit(result.UncPath, 260)!;
        if (result.Operation != PrinterSyncOperation.Present || state.LastOperation is null)
        {
            state.LastOperation = result.Operation.ToString();
        }
        state.Status = result.Succeeded
            ? ClientPrinterStatuses.Present
            : result.Operation == PrinterSyncOperation.Removed
                ? ClientPrinterStatuses.RemovalFailed
                : ClientPrinterStatuses.Failed;
        state.LastError = Limit(result.Error, 1024);

        var changed = priorStatus != state.Status
                      || !string.Equals(priorError, state.LastError, StringComparison.Ordinal);
        if (changed || result.Operation is PrinterSyncOperation.Installed or PrinterSyncOperation.Adopted)
        {
            state.UpdatedAt = now;
        }

        if (result.Operation == PrinterSyncOperation.Installed && result.Succeeded)
        {
            AddActivity(clientUser, "Printer installed", $"{state.DisplayName ?? state.UncPath} was installed.", syncId, now, state.DisplayName, state.UncPath);
        }
        else if (result.Operation == PrinterSyncOperation.Adopted && result.Succeeded)
        {
            AddActivity(clientUser, "Printer adopted", $"{state.DisplayName ?? state.UncPath} was already present and is now managed.", syncId, now, state.DisplayName, state.UncPath);
        }
        else if (!result.Succeeded && changed)
        {
            var type = result.Operation == PrinterSyncOperation.Removed ? "Removal failed" : "Printer failed";
            AddActivity(clientUser, type, $"{state.DisplayName ?? state.UncPath}: {type.ToLowerInvariant()}.", syncId, now, state.DisplayName, state.UncPath, state.LastError);
        }
        else if (result.Succeeded && priorStatus is ClientPrinterStatuses.Failed or ClientPrinterStatuses.RemovalFailed)
        {
            AddActivity(clientUser, "Printer recovered", $"{state.DisplayName ?? state.UncPath} is healthy again.", syncId, now, state.DisplayName, state.UncPath);
        }
    }

    private static async Task<(ClientDeviceEntity Device, ClientUserEntity User, bool IsNewUser)> GetOrCreateAsync(
        AppDbContext db,
        string? machineName,
        string? deviceId,
        string username,
        string? clientVersion,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var displayMachine = string.IsNullOrWhiteSpace(machineName) ? "(unknown)" : Limit(machineName.Trim(), 128)!;
        var normalizedMachine = Normalize(displayMachine, 128);
        var displayUser = Limit(username.Trim(), 256)!;
        var normalizedUser = Normalize(displayUser, 256);
        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId)
            ? null
            : Normalize(deviceId, 128);

        ClientDeviceEntity? device;
        if (normalizedDeviceId is not null)
        {
            device = await db.ClientDevices
                .FirstOrDefaultAsync(d => d.DeviceIdentifier == normalizedDeviceId, ct);
            if (device is not null)
            {
                // A hostname can change without changing the Windows install.
                // Keep the stable device row and update only its display name.
                device.MachineName = displayMachine;
                device.NormalizedMachineName = normalizedMachine;
            }
        }
        else
        {
            // Old clients have no stable identifier. Keep them isolated to
            // legacy rows so a truncated name can never overwrite a new row.
            device = await db.ClientDevices.FirstOrDefaultAsync(
                d => d.DeviceIdentifier == null
                     && d.NormalizedMachineName == normalizedMachine,
                ct);
        }

        if (device is null)
        {
            device = new ClientDeviceEntity
            {
                DeviceIdentifier = normalizedDeviceId,
                MachineName = displayMachine,
                NormalizedMachineName = normalizedMachine,
                ClientVersion = Limit(clientVersion, 64),
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            db.ClientDevices.Add(device);
        }

        var clientUser = await db.ClientUsers
            .FirstOrDefaultAsync(u => u.DeviceId == device.Id && u.NormalizedUsername == normalizedUser, ct);
        if (clientUser is not null)
        {
            return (device, clientUser, false);
        }

        clientUser = new ClientUserEntity
        {
            Device = device,
            Username = displayUser,
            NormalizedUsername = normalizedUser,
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        db.ClientUsers.Add(clientUser);
        return (device, clientUser, true);
    }

    private static void AddActivity(
        ClientUserEntity clientUser,
        string type,
        string summary,
        Guid? syncId,
        DateTimeOffset now,
        string? printerDisplayName = null,
        string? printerUncPath = null,
        string? error = null)
        => clientUser.Activities.Add(new ClientActivityEntity
        {
            ClientUser = clientUser,
            Type = Limit(type, 32)!,
            Summary = Limit(summary, 512)!,
            SyncId = syncId,
            OccurredAt = now,
            PrinterDisplayName = Limit(printerDisplayName, 128),
            PrinterUncPath = Limit(printerUncPath, 260),
            Error = Limit(error, 1024),
        });

    private static string StatusName(SyncReportStatus status) => status switch
    {
        SyncReportStatus.Synced => ClientSyncStatuses.Synced,
        SyncReportStatus.Partial => ClientSyncStatuses.Partial,
        SyncReportStatus.Deferred => ClientSyncStatuses.Deferred,
        SyncReportStatus.Failed => ClientSyncStatuses.Failed,
        _ => ClientSyncStatuses.Unreported,
    };

    private static string Normalize(string value, int maxLength)
        => Limit(value.Trim(), maxLength)!.ToUpperInvariant();

    private static string? Limit(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];
}
