using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Zones;

/// <summary>
/// Exercises <see cref="ZoneService"/> against SQLite <c>:memory:</c> (the same
/// engine as production, unlike the EF in-memory provider). Each service call
/// gets a fresh context from the factory — just like the real Blazor app — so
/// these cover the cross-context tracking behaviour that broke zone editing.
/// </summary>
public sealed class ZoneServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ZoneService _service;

    public ZoneServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = NewContext();
        _db.Database.EnsureCreated();
        _service = new ZoneService(new SharedConnectionFactory(_connection));
    }

    [Fact]
    public async Task Update_KeepingAnAssignedPrinter_Persists_WithoutThrowing()
    {
        var printer = await AddPrinterAsync(@"\\srv\p1", "P1");

        var zoneId = await _service.CreateAsync(new ZoneInput
        {
            Name = "HR",
            Priority = 10,
            PrinterIds = [printer.Id],
            Rules = [new RuleInput { GroupSid = "S-1-5-21-1" }],
        });

        // Rename, keep the same printer, change the rule. Re-adding the already
        // linked printer used to throw a join-entity tracking conflict — the
        // "unknown error" that made editing impossible.
        await _service.UpdateAsync(zoneId, new ZoneInput
        {
            Name = "Human Resources",
            Priority = 20,
            PrinterIds = [printer.Id],
            Rules = [new RuleInput { GroupSid = "S-1-5-21-2" }],
        });

        var zone = await LoadZoneAsync(zoneId);
        Assert.Equal("Human Resources", zone.Name);
        Assert.Equal(20, zone.Priority);
        Assert.Equal(printer.Id, Assert.Single(zone.Printers).Id);
        Assert.Equal("S-1-5-21-2", Assert.Single(zone.Rules).GroupSid);
    }

    [Fact]
    public async Task Update_ReplacesPrinterAssignments()
    {
        var p1 = await AddPrinterAsync(@"\\srv\p1", "P1");
        var p2 = await AddPrinterAsync(@"\\srv\p2", "P2");

        var zoneId = await _service.CreateAsync(new ZoneInput
        {
            Name = "Lab",
            PrinterIds = [p1.Id],
            Rules = [new RuleInput { GroupSid = "S-1-5-21-9" }],
        });

        // Swap p1 -> p2: p1 removed, p2 added, neither a no-op re-add.
        await _service.UpdateAsync(zoneId, new ZoneInput
        {
            Name = "Lab",
            PrinterIds = [p2.Id],
            Rules = [new RuleInput { GroupSid = "S-1-5-21-9" }],
        });

        var zone = await LoadZoneAsync(zoneId);
        Assert.Equal(p2.Id, Assert.Single(zone.Printers).Id);
    }

    [Fact]
    public async Task Update_CanClearAllPrinters()
    {
        var printer = await AddPrinterAsync(@"\\srv\p1", "P1");
        var zoneId = await _service.CreateAsync(new ZoneInput
        {
            Name = "Reception",
            PrinterIds = [printer.Id],
            Rules = [new RuleInput { GroupSid = "S-1-5-21-3" }],
        });

        await _service.UpdateAsync(zoneId, new ZoneInput
        {
            Name = "Reception",
            PrinterIds = [],
            Rules = [new RuleInput { GroupSid = "S-1-5-21-3" }],
        });

        var zone = await LoadZoneAsync(zoneId);
        Assert.Empty(zone.Printers);
    }

    private async Task<PrinterEntity> AddPrinterAsync(string unc, string name)
    {
        var printer = new PrinterEntity { UncPath = unc, DisplayName = name };
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();
        return printer;
    }

    private async Task<ZoneEntity> LoadZoneAsync(Guid id)
    {
        await using var freshDb = NewContext();
        return await freshDb.Zones
            .Include(z => z.Rules)
            .Include(z => z.Printers)
            .SingleAsync(z => z.Id == id);
    }

    private AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>Hands out contexts over the one shared in-memory connection.</summary>
    private sealed class SharedConnectionFactory : IDbContextFactory<AppDbContext>
    {
        private readonly SqliteConnection _connection;

        public SharedConnectionFactory(SqliteConnection connection) => _connection = connection;

        public AppDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new AppDbContext(options);
        }
    }
}
