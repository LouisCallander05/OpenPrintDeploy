using System.Net;
using System.Net.Http.Json;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class PrinterConnectionMigrationTests
{
    private static readonly PrinterDto Printer = new("Library", @"\\srv\library");

    [Theory]
    [InlineData(PrinterOrigin.Created)]
    [InlineData(PrinterOrigin.Adopted)]
    public async Task LegacyManagedConnectionsMigrateOnceAcrossRestartsAndClientUpdates(PrinterOrigin origin)
    {
        var repairs = new Repairs();
        var sync = Create(new([Printer]), new Applier(), repairs);
        var first = await sync.SyncOnceAsync("PC", [new(Printer.UncPath, origin)]);
        var migrated = Assert.Single(first.ManagedPrinters);
        Assert.Equal(origin, migrated.Origin);
        Assert.Equal(1, migrated.ConnectionRevision);
        Assert.Single(repairs.Calls);

        var persisted = ManagedPrinterSerializer.Parse(ManagedPrinterSerializer.Serialize(first.ManagedPrinters));
        await Create(new([Printer]), new Applier(), repairs).SyncOnceAsync("PC", persisted, clientVersion: "99.0.0");
        Assert.Single(repairs.Calls);
    }

    [Fact]
    public async Task NewConnectionsAndNewlyAdoptedQueuesAreNotLegacyMigrations()
    {
        var addedRepairs = new Repairs();
        var added = await Create(new([Printer]), new Applier(missing: true), addedRepairs).SyncOnceAsync("PC", []);
        Assert.Equal(1, Assert.Single(added.ManagedPrinters).ConnectionRevision);
        Assert.Empty(addedRepairs.Calls);

        var adoptedRepairs = new Repairs();
        var adopted = await Create(new([Printer]), new Applier(), adoptedRepairs).SyncOnceAsync("PC", []);
        Assert.Equal(1, Assert.Single(adopted.ManagedPrinters).ConnectionRevision);
        Assert.Empty(adoptedRepairs.Calls);
    }

    [Fact]
    public async Task FailedRepairStaysPendingAndRetriesWithoutLosingProvenance()
    {
        var other = new PrinterDto("Other", @"\\srv\other");
        var repairs = new Repairs { Fail = true };
        var sync = Create(new([Printer, other], SyncId: Guid.NewGuid()), new Applier(), repairs);
        var first = await sync.SyncOnceAsync("PC", [new(Printer.UncPath, PrinterOrigin.Adopted), new(other.UncPath, PrinterOrigin.Created)]);
        Assert.Equal(SyncReportStatus.Partial, first.ReportStatus);
        Assert.Single(first.Failures);
        Assert.Equal(0, first.ManagedPrinters.Single(m => m.Unc == Printer.UncPath).ConnectionRevision);
        Assert.Equal(1, first.ManagedPrinters.Single(m => m.Unc == other.UncPath).ConnectionRevision);

        repairs.Fail = false;
        var second = await sync.SyncOnceAsync("PC", first.ManagedPrinters);
        Assert.All(second.ManagedPrinters, m => Assert.Equal(1, m.ConnectionRevision));
        Assert.Equal(PrinterOrigin.Adopted, second.ManagedPrinters.Single(m => m.Unc == Printer.UncPath).Origin);
        Assert.Equal(3, repairs.Calls.Count); // Failed printer retried, successful one skipped.
    }

    [Fact]
    public async Task NonAuthoritativeOrRemovedPrintersAreNeverRepaired()
    {
        var repairs = new Repairs();
        ManagedPrinter[] previous = [new(Printer.UncPath, PrinterOrigin.Created)];
        await Create(new([Printer], Authoritative: false), new Applier(), repairs).SyncOnceAsync("PC", previous);
        await Create(new([Printer], RemovePrinters: [Printer.UncPath]), new Applier(), repairs).SyncOnceAsync("PC", previous);
        await Create(new([]), new Applier(), repairs).SyncOnceAsync("PC", previous);
        Assert.Empty(repairs.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrFailedDriverPreparationDoesNotDisconnectQueue(bool failure)
    {
        var repairs = new Repairs();
        var sync = Create(new([Printer]), new Applier(), repairs, new Drivers { Fail = failure, SupplyDriver = false });
        var result = await sync.SyncOnceAsync("PC", [new(Printer.UncPath, PrinterOrigin.Created)]);
        Assert.Empty(repairs.Calls);
        Assert.Equal(0, Assert.Single(result.ManagedPrinters).ConnectionRevision);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task ReaddedLegacyConnectionRequestsPreferenceRecoveryWithoutSecondReconnect()
    {
        var repairs = new Repairs();
        await Create(new([Printer]), new Applier(missing: true), repairs)
            .SyncOnceAsync("PC", [new(Printer.UncPath, PrinterOrigin.Created)]);
        Assert.True(Assert.Single(repairs.Calls).JustAdded);
    }

    [Fact]
    public async Task FailedAddDoesNotAttemptMigration()
    {
        var repairs = new Repairs();
        var result = await Create(new([Printer]), new Applier(missing: true, fail: true), repairs)
            .SyncOnceAsync("PC", [new(Printer.UncPath, PrinterOrigin.Created)]);
        Assert.Empty(repairs.Calls);
        Assert.Equal(0, Assert.Single(result.ManagedPrinters).ConnectionRevision);
    }

    private static SyncOrchestrator Create(SyncResponseDto response, Applier applier, Repairs repairs, Drivers? drivers = null)
        => new(new SyncApiClient(new HttpClient(new Handler(response)) { BaseAddress = new Uri("https://test") }),
            applier, drivers ?? new Drivers(), repairs);

    private sealed class Handler(SyncResponseDto response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
    }

    private sealed class Applier(bool missing = false, bool fail = false) : IPrinterApplier
    {
        public Task<IReadOnlyList<string>> EnumerateInstalledAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(missing ? [] : [Printer.UncPath, @"\\srv\other"]);
        public Task<ApplyOutcome> ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
            => Task.FromResult(new ApplyOutcome(fail ? [] : plan.ToAdd,
                fail ? plan.ToAdd.Select(p => new PrinterApplyError(p, "Add failed")).ToList() : [],
                plan.ToRemove.Select(u => new PrinterRemovalResult(u)).ToList(), []));
    }

    private sealed class Drivers : IDriverPreparer
    {
        public bool Fail { get; init; }
        public bool SupplyDriver { get; init; } = true;
        public Task<DriverPreparationOutcome> PrepareAsync(IReadOnlyList<PrinterDto> printers, CancellationToken ct)
            => Task.FromResult(new DriverPreparationOutcome(Fail ? printers.Select(p => new PrinterApplyError(p, "Driver failed")).ToList() : [],
                SupplyDriver ? printers.ToDictionary(p => p.UncPath, _ => new PreparedDriver("Driver", "Windows x64", 1, 2), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, PreparedDriver>(StringComparer.OrdinalIgnoreCase)));
    }

    private sealed class Repairs : IPrinterConnectionRepairer
    {
        public bool Fail { get; set; }
        public List<(string Unc, bool JustAdded)> Calls { get; } = [];
        public Task CheckAndRepairAsync(PrinterDto printer, PreparedDriver driver, CancellationToken ct, bool connectionJustAdded = false, Guid? requestId = null)
        {
            Calls.Add((printer.UncPath, connectionJustAdded));
            if (Fail && printer.UncPath == Printer.UncPath) throw new IOException("Busy");
            return Task.CompletedTask;
        }
    }
}
