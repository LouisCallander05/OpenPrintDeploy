using System.Net;
using System.Net.Http.Json;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class SyncOrchestratorTests
{
    [Fact]
    public async Task SyncOnce_AppliesDiff_AndReturnsNewManagedSet()
    {
        var server = new SyncResponseDto(
            [new PrinterDto("HR", @"\\srv\a"), new PrinterDto("Lobby", @"\\srv\b")]);
        var api = new SyncApiClient(StubHttpClient(server));
        var applier = new RecordingApplier(installed: [@"\\srv\b"]);
        var orchestrator = new SyncOrchestrator(api, applier);

        var managedAfter = await orchestrator.SyncOnceAsync("PC1", [@"\\srv\b", @"\\srv\c"]);

        Assert.NotNull(applier.Applied);
        Assert.Equal([@"\\srv\a"], applier.Applied!.ToAdd.Select(p => p.UncPath));
        Assert.Equal([@"\\srv\c"], applier.Applied.ToRemove);
        Assert.Equal([@"\\srv\a", @"\\srv\b"], managedAfter.ManagedUncs.OrderBy(u => u));
        Assert.Equal(["HR"], managedAfter.AddedNames);
    }

    [Fact]
    public async Task SyncOnce_ReinstallsMissingPrinters_UsingCurrentConnections()
    {
        var server = new SyncResponseDto(
            [new PrinterDto("HR", @"\\srv\a"), new PrinterDto("Lobby", @"\\srv\b")]);
        var api = new SyncApiClient(StubHttpClient(server));
        var applier = new RecordingApplier(installed: [@"\\srv\b"]);
        var orchestrator = new SyncOrchestrator(api, applier);

        await orchestrator.SyncOnceAsync("PC1", [@"\\srv\a", @"\\srv\b"]);

        Assert.Equal([@"\\srv\a"], applier.Applied!.ToAdd.Select(p => p.UncPath));
    }

    [Fact]
    public async Task SyncOnce_OneFailedAdd_InstallsTheRest_AndOnlyManagesWhatSucceeded()
    {
        // Two desired printers; one fails to install (e.g. clashes with an
        // orphaned printer). The other must still install, only the succeeded one
        // is managed, and the failure is reported.
        var server = new SyncResponseDto(
            [new PrinterDto("HR", @"\\srv\a"), new PrinterDto("Bad", @"\\srv\bad")]);
        var api = new SyncApiClient(StubHttpClient(server));
        var applier = new RecordingApplier(installed: [], failUncs: [@"\\srv\bad"]);
        var orchestrator = new SyncOrchestrator(api, applier);

        var result = await orchestrator.SyncOnceAsync("PC1", []);

        Assert.Equal([@"\\srv\a"], result.ManagedUncs);
        Assert.Equal(["HR"], result.AddedNames);
        Assert.Equal(["Bad"], result.FailedNames);
    }

    [Fact]
    public async Task SyncOnce_NonAuthoritativeResponse_RemovesNothing_AndKeepsManagedSet()
    {
        // Server couldn't resolve the user (directory outage) → non-authoritative
        // empty set. The client must keep its printers, not uninstall them.
        var api = new SyncApiClient(StubHttpClient(new SyncResponseDto([], Authoritative: false)));
        var applier = new RecordingApplier(installed: [@"\\srv\a", @"\\srv\b"]);
        var orchestrator = new SyncOrchestrator(api, applier);

        var managedAfter = await orchestrator.SyncOnceAsync("PC1", [@"\\srv\a", @"\\srv\b"]);

        Assert.Empty(applier.Applied!.ToAdd);
        Assert.Empty(applier.Applied.ToRemove);
        Assert.Equal([@"\\srv\a", @"\\srv\b"], managedAfter.ManagedUncs.OrderBy(u => u));
        Assert.Empty(managedAfter.AddedNames);
    }

    [Fact]
    public async Task SyncOnce_PropagatesTransportFailure()
    {
        var api = new SyncApiClient(new HttpClient(new ErrorHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        });
        var orchestrator = new SyncOrchestrator(api, new RecordingApplier());

        await Assert.ThrowsAsync<HttpRequestException>(() => orchestrator.SyncOnceAsync(null, []));
    }

    private static HttpClient StubHttpClient(SyncResponseDto response)
    {
        var message = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response),
        };
        return new HttpClient(new StubHandler(message)) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class RecordingApplier : IPrinterApplier
    {
        private readonly IReadOnlyList<string> _installed;
        private readonly HashSet<string> _failUncs;

        public RecordingApplier(IReadOnlyList<string>? installed = null, IEnumerable<string>? failUncs = null)
        {
            _installed = installed ?? [];
            _failUncs = new HashSet<string>(failUncs ?? [], StringComparer.OrdinalIgnoreCase);
        }

        public ReconcileResult? Applied { get; private set; }

        public Task<ApplyOutcome> ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
        {
            Applied = plan;
            var added = plan.ToAdd.Where(p => !_failUncs.Contains(p.UncPath)).ToList();
            var failed = plan.ToAdd
                .Where(p => _failUncs.Contains(p.UncPath))
                .Select(p => new PrinterApplyError(p, "simulated failure"))
                .ToList();
            return Task.FromResult(new ApplyOutcome(added, failed));
        }

        public Task<IReadOnlyList<string>> EnumerateInstalledAsync(CancellationToken ct = default)
            => Task.FromResult(_installed);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
