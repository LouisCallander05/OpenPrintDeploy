using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Server.Sync;

/// <summary>
/// Resolves a sync request into the printer set the client should install.
/// Pulls zones from EF, runs the pure <see cref="ZoneEvaluator"/>, then
/// hydrates the resulting printer IDs back to UNC paths for the wire DTO.
/// </summary>
public sealed class SyncHandler
{
    private readonly AppDbContext _db;
    private readonly ZoneRepository _zones;
    private readonly ILogger<SyncHandler> _logger;

    public SyncHandler(AppDbContext db, ZoneRepository zones, ILogger<SyncHandler> logger)
    {
        _db = db;
        _zones = zones;
        _logger = logger;
    }

    public async Task<SyncResponseDto> HandleAsync(
        SyncRequestDto request,
        IPAddress? connectionIp,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Caller-supplied IP wins, falling back to the connection's remote IP.
        // Real deployments should prefer connection IP and ignore the body's
        // value once auth is wired.
        var clientIp = ParseIp(request.ClientIp) ?? connectionIp;

        var context = new EvaluationContext(
            UserGroupSids: request.GroupSids.ToHashSet(StringComparer.Ordinal),
            MachineOuDn: request.MachineOuDn,
            ClientIp: clientIp);

        var zones = await _zones.LoadAllAsync(ct);
        var result = ZoneEvaluator.Evaluate(context, zones);

        if (result.PrinterIds.Count == 0)
        {
            _logger.LogInformation(
                "Sync for {User}: no matching zones (groups={GroupCount}, ou={Ou}, ip={Ip})",
                request.Username, request.GroupSids.Count, request.MachineOuDn, clientIp);
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
            request.Username, printerDtos.Count, defaultUnc ?? "(none)");

        return new SyncResponseDto(printerDtos, defaultUnc);
    }

    private static IPAddress? ParseIp(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null
           : IPAddress.TryParse(raw, out var ip) ? ip
           : null;
}
