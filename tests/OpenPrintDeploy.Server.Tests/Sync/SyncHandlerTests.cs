using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Data.Entities;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Server.Zones;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

/// <summary>
/// Covers the cross-domain fix: <see cref="SyncHandler"/> matches on the group
/// SIDs carried in the authenticated token (so a user from another trusted
/// domain resolves without any directory lookup), and falls back to the
/// directory only when the token has no group claims (the dev header-auth path).
/// </summary>
public sealed class SyncHandlerTests : IAsyncDisposable
{
    private const string Sid = "S-1-5-21-100-512";
    private const string Unc = @"\\srv\staff-mfp";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public SyncHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = NewContext();
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Handle_MatchesOnTokenGroups_WithoutConsultingDirectory()
    {
        await SeedZoneAsync(Sid, Unc);
        var directory = new FakeDirectory(mustNotBeCalled: true);
        var handler = NewHandler(directory);

        // A user from EDU001 reaching an EDU002-joined server: the token carries
        // the group, so no directory lookup is needed.
        var response = await handler.HandleAsync(Principal(@"EDU001\jsmith", Sid), null, default);

        Assert.Contains(response.Printers, p => p.UncPath == Unc);
        Assert.False(directory.WasCalled);
    }

    [Fact]
    public async Task Handle_FallsBackToDirectory_WhenTokenHasNoGroups()
    {
        await SeedZoneAsync(Sid, Unc);
        var directory = new FakeDirectory(groups: Sid);
        var handler = NewHandler(directory);

        // Only a name claim (the dev header-auth shape) — no group SIDs.
        var response = await handler.HandleAsync(Principal(@"EDU002\student"), null, default);

        Assert.Contains(response.Printers, p => p.UncPath == Unc);
        Assert.True(directory.WasCalled);
    }

    [Fact]
    public async Task Handle_ReturnsEmpty_WhenNoTokenGroupMatches()
    {
        await SeedZoneAsync(Sid, Unc);
        var handler = NewHandler(new FakeDirectory(mustNotBeCalled: true));

        var response = await handler.HandleAsync(Principal(@"EDU001\jsmith", "S-1-5-21-999-1"), null, default);

        Assert.Empty(response.Printers);
        Assert.True(response.Authoritative);
    }

    [Fact]
    public async Task Handle_ReturnsNonAuthoritative_WhenDirectoryUnavailable()
    {
        await SeedZoneAsync(Sid, Unc);
        // Token carries no groups, so the handler falls back to the directory —
        // which is in an outage. The response must NOT be an authoritative empty
        // set, or the client would uninstall every printer it manages.
        var handler = NewHandler(new FakeDirectory(available: false));

        var response = await handler.HandleAsync(Principal(@"EDU002\student"), null, default);

        Assert.Empty(response.Printers);
        Assert.False(response.Authoritative);
    }

    [Fact]
    public async Task Handle_ReturnsAuthoritativeEmpty_WhenDirectoryHasNoGroups()
    {
        await SeedZoneAsync(Sid, Unc);
        // Directory is reachable but the user genuinely has no groups: an
        // authoritative empty set, so the client legitimately removes printers.
        var handler = NewHandler(new FakeDirectory());

        var response = await handler.HandleAsync(Principal(@"EDU002\student"), null, default);

        Assert.Empty(response.Printers);
        Assert.True(response.Authoritative);
    }

    [Fact]
    public async Task Handle_ReturnsGlobalPrinterRemovals()
    {
        const string retired = @"\\oldserver\retired";
        _db.RemovedPrinters.Add(new RemovedPrinterEntity { UncPath = retired });
        await _db.SaveChangesAsync();
        var handler = NewHandler(new FakeDirectory());

        var response = await handler.HandleAsync(Principal(@"EDU002\student"), null, default);

        Assert.Contains(retired, response.RemovePrinters!);
    }

    private SyncHandler NewHandler(IDirectoryService directory)
    {
        var db = NewContext();
        return new SyncHandler(db, new ZoneRepository(db), directory, NullLogger<SyncHandler>.Instance);
    }

    private async Task SeedZoneAsync(string ruleSid, string unc)
    {
        var zone = new ZoneEntity
        {
            Name = "Staff",
            Rules = [new ZoneRuleEntity { GroupSid = ruleSid }],
            Printers = [new PrinterEntity { UncPath = unc, DisplayName = "Staff MFP" }],
        };
        _db.Zones.Add(zone);
        await _db.SaveChangesAsync();
    }

    private static ClaimsPrincipal Principal(string name, params string[] groupSids)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(groupSids.Select(s => new Claim(ClaimTypes.GroupSid, s)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Negotiate"));
    }

    private AppDbContext NewContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class FakeDirectory : IDirectoryService
    {
        private readonly IReadOnlySet<string>? _groups;
        private readonly bool _mustNotBeCalled;
        private readonly bool _available;

        public FakeDirectory(string? groups = null, bool mustNotBeCalled = false, bool available = true)
        {
            _groups = groups is null ? null : new HashSet<string>([groups], StringComparer.Ordinal);
            _mustNotBeCalled = mustNotBeCalled;
            _available = available;
        }

        public bool WasCalled { get; private set; }

        public Task<GroupResolution> GetGroupSidsAsync(string username, CancellationToken ct = default)
        {
            WasCalled = true;
            if (_mustNotBeCalled)
            {
                throw new InvalidOperationException("Directory must not be consulted when the token carries groups.");
            }

            return Task.FromResult(_available
                ? GroupResolution.Resolved(_groups ?? new HashSet<string>(StringComparer.Ordinal))
                : GroupResolution.Unavailable);
        }

        public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DirectoryGroup>>([]);

        public Task<string?> ResolveGroupNameAsync(string sid, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<DirectoryDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
            => Task.FromResult(new DirectoryDiagnostics("Fake", "None", null, null, true, 0, null));
    }
}
