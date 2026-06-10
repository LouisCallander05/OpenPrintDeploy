namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// One sync cycle: fetch the resolved set, diff against what we deployed last
/// time, and apply the difference. Returns the new managed UNC set for the
/// caller to persist. Any failure propagates — the caller keeps the previous
/// managed set and leaves installed printers in place (degrade gracefully when
/// the server/DC is unreachable).
/// </summary>
public sealed class SyncOrchestrator
{
    private readonly SyncApiClient _api;
    private readonly IPrinterApplier _applier;

    public SyncOrchestrator(SyncApiClient api, IPrinterApplier applier)
    {
        _api = api;
        _applier = applier;
    }

    public async Task<IReadOnlyList<string>> SyncOnceAsync(
        string? machineName,
        IReadOnlyCollection<string> previouslyManaged,
        CancellationToken ct = default)
    {
        var desired = await _api.FetchAsync(machineName, ct);
        var currentlyInstalled = await _applier.EnumerateInstalledAsync(ct);
        var plan = PrinterReconciler.Reconcile(desired, previouslyManaged, currentlyInstalled);
        await _applier.ApplyAsync(plan, ct);

        return previouslyManaged
            .Where(unc => !plan.ToRemove.Contains(unc, StringComparer.OrdinalIgnoreCase))
            .Concat(plan.ToAdd.Select(p => p.UncPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
