using System.Net;
using System.Net.Http.Json;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class RemotePrinterRepairTests
{
    private static readonly PrinterDto A = new("A", @"\\srv\a");
    private static readonly PrinterDto B = new("B", @"\\srv\b");
    private static readonly Dictionary<string, PreparedDriver> Drivers = new(StringComparer.OrdinalIgnoreCase)
    {
        [A.UncPath] = new("Driver", "Windows x64", 1, 2), [B.UncPath] = new("Driver", "Windows x64", 1, 2)
    };

    [Fact]
    public async Task LostAcknowledgementAndClientRestartDoNotRepeatCompletedConnections()
    {
        var id = Guid.NewGuid();
        var state = new State();
        var repair = new Repairs();
        var handler = new Handler { DropReports = true };
        await Runner(handler, repair, state).RunAsync(id, "DEVICE", [A, B], Drivers, [], [], default);
        handler.DropReports = false;
        await Runner(handler, repair, state).RunAsync(id, "DEVICE", [A, B], Drivers, [], [], default);
        Assert.Equal(2, repair.Calls.Count);
        Assert.All(repair.Calls, call => Assert.Equal(id, call.Request));
        Assert.Equal(PrinterRepairStatus.Completed, handler.Reports.Last().Status);
    }

    [Fact]
    public async Task RetrySameRequestOnlyRepairsFailuresWhileNewRequestRepairsAll()
    {
        var id = Guid.NewGuid();
        var state = new State();
        var repair = new Repairs { FailB = true };
        var handler = new Handler();
        var runner = Runner(handler, repair, state);
        var first = await runner.RunAsync(id, "DEVICE", [A, B], Drivers, [], [], default);
        Assert.Single(first.Failures);
        Assert.Equal(PrinterRepairStatus.Failed, handler.Reports.Last().Status);
        repair.FailB = false;
        await runner.RunAsync(id, "DEVICE", [A, B], Drivers, [], [], default);
        Assert.Equal(3, repair.Calls.Count);
        await runner.RunAsync(Guid.NewGuid(), "DEVICE", [A, B], Drivers, [], [], default);
        Assert.Equal(5, repair.Calls.Count);
    }

    [Fact]
    public async Task DriverAndAddFailuresBlockOnlyTheirOwnQueues()
    {
        var repair = new Repairs();
        var handler = new Handler();
        var result = await Runner(handler, repair, new State()).RunAsync(Guid.NewGuid(), "DEVICE", [A, B], Drivers,
            [new(A, "Driver preparation failed")], [B.UncPath], default);
        Assert.Equal(B.UncPath, Assert.Single(repair.Calls).Unc);
        Assert.True(repair.Calls[0].JustAdded);
        Assert.Empty(result.Failures); // The original failure is already in the sync report.
        Assert.Equal(PrinterRepairStatus.Failed, handler.Reports.Last().Status);
    }

    [Fact]
    public async Task FailedProgressWriteLeavesNativeBackupEligibleForRecovery()
    {
        var repair = new Repairs();
        var state = new State { FailSave = true };
        var runner = Runner(new Handler(), repair, state);
        var first = await runner.RunAsync(Guid.NewGuid(), "DEVICE", [A], Drivers, [], [], default);
        Assert.Empty(first.Completed);
        Assert.Single(first.Failures);
    }

    [Fact]
    public async Task CorruptProgressFailsSafelyWithoutDuplicatingExistingDriverFailures()
    {
        var repairs = new Repairs();
        var result = await Runner(new Handler(), repairs, new State { FailLoad = true })
            .RunAsync(Guid.NewGuid(), "DEVICE", [A, B], Drivers, [new(A, "Driver failed")], [], default);
        Assert.Empty(repairs.Calls);
        Assert.Equal(B, Assert.Single(result.Failures).Printer);
    }

    private static RemotePrinterRepairRunner Runner(Handler handler, Repairs repairs, State state)
        => new(new SyncApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://test") }), repairs, state);
    private sealed class Handler : HttpMessageHandler
    {
        public bool DropReports { get; set; }
        public List<PrinterRepairReportDto> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (DropReports) throw new HttpRequestException("Offline");
            Reports.Add((await request.Content!.ReadFromJsonAsync<PrinterRepairReportDto>(ct))!);
            return new(HttpStatusCode.NoContent);
        }
    }
    private sealed class State : IRemoteRepairStateStore
    {
        public bool FailSave { get; init; }
        public bool FailLoad { get; init; }
        private readonly Dictionary<Guid, IReadOnlyCollection<string>> _completed = [];
        public IReadOnlyCollection<string> Load(Guid id) => FailLoad ? throw new IOException("Corrupt progress") : _completed.GetValueOrDefault(id) ?? [];
        public void Save(Guid id, IReadOnlyCollection<string> completed)
        {
            if (FailSave) throw new IOException("Disk unavailable");
            _completed[id] = completed;
        }
    }
    private sealed class Repairs : IPrinterConnectionRepairer
    {
        public bool FailB { get; set; }
        public List<(string Unc, Guid? Request, bool JustAdded)> Calls { get; } = [];
        public Task CheckAndRepairAsync(PrinterDto printer, PreparedDriver driver, CancellationToken ct,
            bool connectionJustAdded = false, Guid? requestId = null)
        {
            Calls.Add((printer.UncPath, requestId, connectionJustAdded));
            if (FailB && printer == B) throw new IOException("Busy");
            return Task.CompletedTask;
        }
    }
}
