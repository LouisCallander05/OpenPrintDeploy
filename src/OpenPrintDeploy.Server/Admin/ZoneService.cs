using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;

namespace OpenPrintDeploy.Server.Admin;

/// <summary>
/// CRUD operations for zones, including their rules and printer assignments.
/// Uses short-lived contexts from the factory for the same reason as
/// <see cref="PrinterService"/>.
/// </summary>
public sealed class ZoneService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ZoneService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ZoneEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .Include(z => z.Printers)
            .OrderByDescending(z => z.Priority)
            .ThenBy(z => z.Name)
            .ToListAsync(ct);
    }

    public async Task<ZoneEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Zones
            .AsNoTracking()
            .Include(z => z.Rules)
            .Include(z => z.Printers)
            .FirstOrDefaultAsync(z => z.Id == id, ct);
    }

    public async Task<Guid> CreateAsync(ZoneInput input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await EnsureNameIsUniqueAsync(db, input.Name, excludingId: null, ct);

        var (rules, printerIds) = Normalize(input);
        var printers = await LoadPrintersAsync(db, printerIds, ct);

        var zone = new ZoneEntity
        {
            Name = input.Name.Trim(),
            Priority = input.Priority,
            Rules = rules,
            Printers = printers,
        };
        db.Zones.Add(zone);
        await db.SaveChangesAsync(ct);
        return zone.Id;
    }

    public async Task UpdateAsync(Guid id, ZoneInput input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Zones
            .Include(z => z.Rules)
            .Include(z => z.Printers)
            .FirstOrDefaultAsync(z => z.Id == id, ct)
            ?? throw new AdminValidationException("That zone no longer exists.");

        await EnsureNameIsUniqueAsync(db, input.Name, excludingId: id, ct);

        var (rules, printerIds) = Normalize(input);
        var printers = await LoadPrintersAsync(db, printerIds, ct);

        zone.Name = input.Name.Trim();
        zone.Priority = input.Priority;

        // Replace rules wholesale — simplest correct reconciliation for a
        // handful of rules per zone.
        db.ZoneRules.RemoveRange(zone.Rules);
        zone.Rules = rules;

        zone.Printers.Clear();
        foreach (var printer in printers)
        {
            zone.Printers.Add(printer);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (zone is null)
        {
            return;
        }

        db.Zones.Remove(zone);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Drops empty rules (a rule with no group is meaningless) and de-duplicates
    /// printer ids. Throws if nothing usable remains.
    /// </summary>
    private static (List<ZoneRuleEntity> Rules, List<Guid> PrinterIds) Normalize(ZoneInput input)
    {
        var rules = new List<ZoneRuleEntity>();
        foreach (var r in input.Rules)
        {
            if (r.IsEmpty)
            {
                continue;
            }

            rules.Add(new ZoneRuleEntity { GroupSid = r.GroupSid!.Trim() });
        }

        if (rules.Count == 0)
        {
            throw new AdminValidationException("A zone needs at least one rule with a group set.");
        }

        var printerIds = input.PrinterIds.Distinct().ToList();
        return (rules, printerIds);
    }

    private static async Task<List<PrinterEntity>> LoadPrintersAsync(
        AppDbContext db, IReadOnlyList<Guid> printerIds, CancellationToken ct)
    {
        if (printerIds.Count == 0)
        {
            return [];
        }

        return await db.Printers.Where(p => printerIds.Contains(p.Id)).ToListAsync(ct);
    }

    private static async Task EnsureNameIsUniqueAsync(
        AppDbContext db, string name, Guid? excludingId, CancellationToken ct)
    {
        var trimmed = name.Trim();
        var clash = await db.Zones
            .AnyAsync(z => z.Name == trimmed && (excludingId == null || z.Id != excludingId), ct);
        if (clash)
        {
            throw new AdminValidationException($"A zone named '{trimmed}' already exists.");
        }
    }
}
