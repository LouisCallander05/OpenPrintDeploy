using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;

namespace OpenPrintDeploy.Server.Admin;

public sealed class RemovedPrinterService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public RemovedPrinterService(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<RemovedPrinterEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.RemovedPrinters.AsNoTracking().OrderBy(p => p.UncPath).ToListAsync(ct);
    }

    public async Task<Guid> CreateAsync(RemovedPrinterInput input, CancellationToken ct = default)
    {
        var unc = input.UncPath.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var normalized = unc.ToUpper();
        if (await db.RemovedPrinters.AnyAsync(p => p.UncPath.ToUpper() == normalized, ct))
        {
            throw new AdminValidationException($"'{unc}' is already on the removal list.");
        }

        if (await db.Printers.AnyAsync(p => p.UncPath.ToUpper() == normalized, ct))
        {
            throw new AdminValidationException(
                $"'{unc}' is configured for deployment. Remove it from Printers first.");
        }

        var entity = new RemovedPrinterEntity { UncPath = unc };
        db.RemovedPrinters.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.RemovedPrinters.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return;
        db.RemovedPrinters.Remove(entity);
        await db.SaveChangesAsync(ct);
    }
}
