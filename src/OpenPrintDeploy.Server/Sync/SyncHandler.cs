using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Server.Sync;

/// <summary>
/// Resolves a sync request into the printer set the client should install.
/// Identity is established by the caller (the authenticated connection); this
/// handler resolves the user's groups and the machine's OU via the directory,
/// runs the pure <see cref="ZoneEvaluator"/>, then hydrates printer IDs to UNCs.
/// </summary>
public sealed class SyncHandler
{
    private readonly AppDbContext _db;
    private readonly ZoneRepository _zones;
    private readonly IDirectoryService _directory;
    private readonly ILogger<SyncHandler> _logger;

    public SyncHandler(
        AppDbContext db,
        ZoneRepository zones,
        IDirectoryService directory,
        ILogger<SyncHandler> logger)
    {
        _db = db;
        _zones = zones;
        _directory = directory;
        _logger = logger;
    }

    public async Task<SyncResponseDto> HandleAsync(
        string username,
        string? machineName,
        IPAddress? connectionIp,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        // Group membership comes from the directory, never the client.
        var groupSids = await _directory.GetGroupSidsAsync(username, ct);
        var machineOuDn = string.IsNullOrWhiteSpace(machineName)
            ? null
            : await _directory.GetMachineOuDnAsync(machineName, ct);

        var context = new EvaluationContext(
            UserGroupSids: groupSids,
            MachineOuDn: machineOuDn,
            ClientIp: connectionIp);

        var zones = await _zones.LoadAllAsync(ct);
        var result = ZoneEvaluator.Evaluate(context, zones);

        if (result.PrinterIds.Count == 0)
        {
            _logger.LogInformation(
                "Sync for {User}: no matching zones (groups={GroupCount}, ou={Ou}, ip={Ip})",
                username, groupSids.Count, machineOuDn, connectionIp);
            return new SyncResponseDto([], null);
        }

        var printers = await _db.Printers
            .AsNoTracking()
            .Where(p => result.PrinterIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.DisplayName,
                p.UncPath,
                p.Location,
            })
            .ToListAsync(ct);

        var printerDtos = printers
            .Select(p => new PrinterDto(p.DisplayName, p.UncPath, p.Location))
            .ToList();

        var defaultUnc = result.DefaultPrinterId is { } defaultId
            ? printers.FirstOrDefault(p => p.Id == defaultId)?.UncPath
            : null;

        _logger.LogInformation(
            "Sync for {User}: returning {Count} printer(s), default={Default}",
            username, printerDtos.Count, defaultUnc ?? "(none)");

        return new SyncResponseDto(printerDtos, defaultUnc);
    }
}
