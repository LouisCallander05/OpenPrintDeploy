using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Server.Directory;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

/// <summary>
/// Covers the "Check a user" diagnostic, which resolves a username the same way
/// /sync does (groups -> matched zones -> printers). A fake directory stands in
/// for AD so the three outcomes are deterministic: a match, no groups at all,
/// and groups that match nothing.
/// </summary>
public sealed class SyncDiagnosticsServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ZoneService _zones;

    public SyncDiagnosticsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = NewContext();
        _db.Database.EnsureCreated();
        _zones = new ZoneService(new SharedConnectionFactory(_connection));
    }

    [Fact]
    public async Task Preview_ReturnsPrinters_WhenAGroupMatchesAZone()
    {
        var printer = await SeedZoneWithPrinterAsync("Staff", "S-1-5-21-100-512");
        var service = new SyncDiagnosticsService(_zones, new FakeDirectory("S-1-5-21-100-512"));

        var preview = await service.PreviewAsync(@"CONTOSO\jsmith");

        Assert.Equal("jsmith", preview.NormalizedAccount);
        Assert.Single(preview.Groups);
        Assert.Equal("Staff", Assert.Single(preview.MatchedZones).Name);
        Assert.Equal(printer.UncPath, Assert.Single(preview.Printers).UncPath);
    }

    [Fact]
    public async Task Preview_NoGroups_YieldsNoPrinters()
    {
        await SeedZoneWithPrinterAsync("Staff", "S-1-5-21-100-512");
        var service = new SyncDiagnosticsService(_zones, new FakeDirectory(/* none */));

        var preview = await service.PreviewAsync(@"CONTOSO\nobody");

        Assert.Empty(preview.Groups);
        Assert.Empty(preview.MatchedZones);
        Assert.Empty(preview.Printers);
    }

    [Fact]
    public async Task Preview_GroupsButNoMatch_YieldsNoPrinters()
    {
        await SeedZoneWithPrinterAsync("Staff", "S-1-5-21-100-512");
        var service = new SyncDiagnosticsService(_zones, new FakeDirectory("S-1-5-21-999-777"));

        var preview = await service.PreviewAsync(@"CONTOSO\jsmith");

        Assert.Single(preview.Groups);
        Assert.Empty(preview.MatchedZones);
        Assert.Empty(preview.Printers);
    }

    private async Task<PrinterEntity> SeedZoneWithPrinterAsync(string zoneName, string ruleSid)
    {
        var printer = new PrinterEntity { UncPath = $@"\\srv\{zoneName}", DisplayName = zoneName };
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();

        await _zones.CreateAsync(new ZoneInput
        {
            Name = zoneName,
            PrinterIds = [printer.Id],
            Rules = [new RuleInput { GroupSid = ruleSid }],
        });
        return printer;
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

    private sealed class SharedConnectionFactory : IDbContextFactory<AppDbContext>
    {
        private readonly SqliteConnection _connection;

        public SharedConnectionFactory(SqliteConnection connection) => _connection = connection;

        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
    }

    /// <summary>Returns a fixed group set for any user; names echo the SID.</summary>
    private sealed class FakeDirectory : IDirectoryService
    {
        private readonly IReadOnlySet<string> _groups;

        public FakeDirectory(params string[] groupSids)
            => _groups = new HashSet<string>(groupSids, StringComparer.Ordinal);

        public Task<GroupResolution> GetGroupSidsAsync(string username, CancellationToken ct = default)
            => Task.FromResult(GroupResolution.Resolved(_groups));

        public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DirectoryGroup>>([]);

        public Task<string?> ResolveGroupNameAsync(string sid, CancellationToken ct = default)
            => Task.FromResult<string?>($"group-{sid}");

        public Task<DirectoryDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
            => Task.FromResult(new DirectoryDiagnostics("Fake", "None", null, null, true, 0, null));
    }
}
