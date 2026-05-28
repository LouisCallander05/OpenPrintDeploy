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
        var applier = new RecordingApplier();
        var orchestrator = new SyncOrchestrator(api, applier);

        var managedAfter = await orchestrator.SyncOnceAsync("PC1", [@"\\srv\b", @"\\srv\c"]);

        Assert.NotNull(applier.Applied);
        Assert.Equal([@"\\srv\a"], applier.Applied!.ToAdd.Select(p => p.UncPath));
        Assert.Equal([@"\\srv\c"], applier.Applied.ToRemove);
        Assert.Equal([@"\\srv\a", @"\\srv\b"], managedAfter.OrderBy(u => u));
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
        public ReconcileResult? Applied { get; private set; }

        public Task ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
        {
            Applied = plan;
            return Task.CompletedTask;
        }
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
