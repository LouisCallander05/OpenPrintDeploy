using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;

namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// Loads zones from persistence and projects them into the evaluator's
/// pure-domain <see cref="Zone"/> shape. Keeps EF concerns out of
/// <see cref="ZoneEvaluator"/>.
/// </summary>
public sealed class ZoneRepository
{
    private readonly AppDbContext _db;

    public ZoneRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Zone>> LoadAllAsync(CancellationToken ct = default)
    {
        // Read-only path: AsNoTracking avoids the change-tracker overhead.
        var entities = await _db.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .Include(z => z.Printers)
            .ToListAsync(ct);

        return entities.Select(MapToDomain).ToList();
    }

    internal static Zone MapToDomain(ZoneEntity entity)
    {
        var rules = entity.Rules
            .Select(r => new ZoneRule(r.GroupSid, r.SubnetCidr, r.OuDn))
            .ToList();
        var printerIds = entity.Printers.Select(p => p.Id).ToList();

        return new Zone(
            Id: entity.Id,
            Name: entity.Name,
            Priority: entity.Priority,
            Rules: rules,
            PrinterIds: printerIds,
            DefaultPrinterId: entity.DefaultPrinterId);
    }
}
