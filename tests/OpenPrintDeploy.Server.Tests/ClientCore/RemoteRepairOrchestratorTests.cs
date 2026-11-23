using System.Net;
using System.Net.Http.Json;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class RemoteRepairOrchestratorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteCommandExcludesRemovedAndPersonalQueuesAndNeedsAuthoritativeState(bool authoritative)
    {
        var a = new PrinterDto("A", @"\\srv\a");
        var b = new PrinterDto("B", @"\\srv\b");
        var handler = new Handler(new([a, b], Authoritative: authoritative, RemovePrinters: [b.UncPath], RepairRequestId: Guid.NewGuid()));
        var repairs = new Repairs();
        var sync = new SyncOrchestrator(new(new HttpClient(handler) { BaseAddress = new Uri("https://test") }),
            new Applier(), new Drivers(), repairs, new State());
        var result = await sync.SyncOnceAsync("PC", [new(a.UncPath, PrinterOrigin.Created)], deviceId: "DEVICE");
        if (authoritative)
        {
            Assert.Equal(a.UncPath, Assert.Single(repairs.Calls)); // No second automatic migration in this sync.
            Assert.Equal(a.UncPath, Assert.Single(result.CompletedConnectionRepairs));
            Assert.Equal(1, Assert.Single(result.ManagedPrinters).ConnectionRevision);
        }
        else
        {
            Assert.Empty(repairs.Calls);
            Assert.Empty(handler.Reports);
        }
    }

    private sealed class Handler(SyncResponseDto response) : HttpMessageHandler
    {
        public List<PrinterRepairReportDto> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/sync/repair-report")
            {
                Reports.Add((await request.Content!.ReadFromJsonAsync<PrinterRepairReportDto>(ct))!);
                return new(HttpStatusCode.NoContent);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
    private sealed class Applier : IPrinterApplier
    {
        public Task<IReadOnlyList<string>> EnumerateInstalledAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([@"\\srv\a", @"\\srv\b", @"\\personal\own"]);
        public Task<ApplyOutcome> ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
            => Task.FromResult(new ApplyOutcome([], [], plan.ToRemove.Select(p => new PrinterRemovalResult(p)).ToList(), []));
    }
    private sealed class Drivers : IDriverPreparer
    {
        public Task<DriverPreparationOutcome> PrepareAsync(IReadOnlyList<PrinterDto> printers, CancellationToken ct)
            => Task.FromResult(new DriverPreparationOutcome([], printers.ToDictionary(p => p.UncPath, _ => new PreparedDriver("Driver", "Windows x64", 1, 2))));
    }
    private sealed class Repairs : IPrinterConnectionRepairer
    {
        public List<string> Calls { get; } = [];
        public Task CheckAndRepairAsync(PrinterDto printer, PreparedDriver driver, CancellationToken ct, bool connectionJustAdded = false, Guid? requestId = null)
        {
            Calls.Add(printer.UncPath);
            return Task.CompletedTask;
        }
    }
    private sealed class State : IRemoteRepairStateStore
    {
        public IReadOnlyCollection<string> Load(Guid id) => [];
        public void Save(Guid id, IReadOnlyCollection<string> completed) { }
    }
}
