using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Server.Zones;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Zones;

/// <summary>
/// Uses SQLite in <c>:memory:</c> mode (not the EF in-memory provider — that
/// has different semantics) so the repository hits the same database engine
/// as production.
/// </summary>
public sealed class ZoneRepositoryTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ZoneRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task LoadAll_ReturnsEmpty_WhenNoZones()
    {
        var repo = new ZoneRepository(_db);

        var zones = await repo.LoadAllAsync();

        Assert.Empty(zones);
    }

    [Fact]
    public async Task LoadAll_MapsRulesAndPrinters()
    {
        var p1 = new PrinterEntity { UncPath = @"\\srv\p1", DisplayName = "P1" };
        var p2 = new PrinterEntity { UncPath = @"\\srv\p2", DisplayName = "P2" };
        var zone = new ZoneEntity
        {
            Name = "Z1",
            Priority = 42,
            Rules =
            [
                new ZoneRuleEntity { GroupSid = "S-1-5-21-1" },
                new ZoneRuleEntity { GroupSid = "S-1-5-21-2" },
            ],
            Printers = [p1, p2],
        };
        _db.Zones.Add(zone);
        await _db.SaveChangesAsync();

        // Fresh context to make sure we're loading from the DB, not the
        // change tracker.
        await using var freshDb = NewContext();
        var repo = new ZoneRepository(freshDb);

        var loaded = await repo.LoadAllAsync();

        var z = Assert.Single(loaded);
        Assert.Equal("Z1", z.Name);
        Assert.Equal(42, z.Priority);
        Assert.Equal(2, z.Rules.Count);
        Assert.Contains(z.Rules, r => r.GroupSid == "S-1-5-21-1");
        Assert.Contains(z.Rules, r => r.GroupSid == "S-1-5-21-2");
        Assert.Equal(2, z.PrinterIds.Count);
        Assert.Contains(p1.Id, z.PrinterIds);
        Assert.Contains(p2.Id, z.PrinterIds);
    }

    [Fact]
    public async Task SeederPopulatesTwoZones()
    {
        await DevDataSeeder.SeedAsync(_db);

        Assert.Equal(2, await _db.Zones.CountAsync());
        Assert.Equal(3, await _db.Printers.CountAsync());
    }

    [Fact]
    public async Task SeederIsIdempotent()
    {
        await DevDataSeeder.SeedAsync(_db);
        await DevDataSeeder.SeedAsync(_db);

        Assert.Equal(2, await _db.Zones.CountAsync());
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
}
