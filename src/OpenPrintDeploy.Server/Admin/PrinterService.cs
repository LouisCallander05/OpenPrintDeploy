using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;

namespace OpenPrintDeploy.Server.Admin;

/// <summary>
/// CRUD operations for printers, backed by short-lived contexts from the
/// <see cref="IDbContextFactory{TContext}"/>. Blazor Server circuits are
/// long-lived, so a context-per-operation avoids the shared-context
/// concurrency pitfalls of a circuit-scoped <see cref="AppDbContext"/>.
/// </summary>
public sealed class PrinterService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PrinterService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<PrinterEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Printers
            .AsNoTracking()
            .OrderBy(p => p.DisplayName)
            .ToListAsync(ct);
    }

    public async Task<PrinterEntity?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Guid> CreateAsync(PrinterInput input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await EnsureUncIsUniqueAsync(db, input.UncPath, excludingId: null, ct);

        var printer = new PrinterEntity
        {
            UncPath = input.UncPath.Trim(),
            DisplayName = input.DisplayName.Trim(),
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(ct);
        return printer.Id;
    }

    public async Task UpdateAsync(Guid id, PrinterInput input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new AdminValidationException("That printer no longer exists.");

        await EnsureUncIsUniqueAsync(db, input.UncPath, excludingId: id, ct);

        printer.UncPath = input.UncPath.Trim();
        printer.DisplayName = input.DisplayName.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (printer is null)
        {
            return;
        }

        db.Printers.Remove(printer);
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureUncIsUniqueAsync(
        AppDbContext db, string uncPath, Guid? excludingId, CancellationToken ct)
    {
        var trimmed = uncPath.Trim();
        var normalized = trimmed.ToUpper();
        if (await db.RemovedPrinters.AnyAsync(p => p.UncPath.ToUpper() == normalized, ct))
        {
            throw new AdminValidationException(
                $"'{trimmed}' is configured for removal. Remove it from the removal list first.");
        }
        var clash = await db.Printers
            .AnyAsync(p => p.UncPath == trimmed && (excludingId == null || p.Id != excludingId), ct);
        if (clash)
        {
            throw new AdminValidationException($"A printer with UNC path '{trimmed}' already exists.");
        }
    }
}
