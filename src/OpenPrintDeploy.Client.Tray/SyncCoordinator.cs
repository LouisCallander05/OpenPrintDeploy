using System.Net.Http;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>Outcome of one sync attempt, for surfacing a tray notification.</summary>
public readonly record struct SyncOutcome(bool Ok, int PrinterCount, string? Error)
{
    public static SyncOutcome Success(int count) => new(true, count, null);

    public static SyncOutcome Failure(string error) => new(false, 0, error);
}

/// <summary>
/// Owns the HTTP client (authenticating as the signed-in user) and runs one
/// sync cycle on demand. Failures are caught and returned, never thrown, so the
/// caller can notify the user and keep installed printers in place when the
/// server or a domain controller is unreachable.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private readonly HttpClient _http;
    private readonly SyncOrchestrator _orchestrator;
    private readonly ManagedStateStore _state;
    private readonly string _machineName;

    public SyncCoordinator(TraySettings settings)
    {
        _http = SyncApiClient.CreateDefaultCredentialsClient(settings.ServerBaseAddress);
        _orchestrator = new SyncOrchestrator(new SyncApiClient(_http), new WindowsPrinterApplier());
        _state = new ManagedStateStore();
        _machineName = Environment.MachineName;
    }

    public async Task<SyncOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var managed = _state.Load();
            var updated = await _orchestrator.SyncOnceAsync(_machineName, managed, ct);
            _state.Save(updated);
            return SyncOutcome.Success(updated.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncOutcome.Failure(ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
