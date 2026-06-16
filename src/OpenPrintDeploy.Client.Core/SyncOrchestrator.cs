namespace OpenPrintDeploy.Client.Core;

/// <param name="ManagedUncs">UNC paths of all printers this client now manages.</param>
/// <param name="AddedNames">Display names of printers added in this cycle (empty when nothing changed).</param>
/// <param name="FailedNames">Display names of printers that failed to install this cycle (best-effort apply).</param>
public sealed record SyncResult(
    IReadOnlyList<string> ManagedUncs,
    IReadOnlyList<string> AddedNames,
    IReadOnlyList<string> FailedNames);

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

    public async Task<SyncResult> SyncOnceAsync(
        string? machineName,
        IReadOnlyCollection<string> previouslyManaged,
        CancellationToken ct = default)
    {
        var desired = await _api.FetchAsync(machineName, ct);
        var currentlyInstalled = await _applier.EnumerateInstalledAsync(ct);
        var plan = PrinterReconciler.Reconcile(desired, previouslyManaged, currentlyInstalled);
        var outcome = await _applier.ApplyAsync(plan, ct);

        // Persist only the printers that actually installed. A printer that
        // failed to add isn't claimed as managed, so the next sync retries it
        // (it's still desired and still not installed) instead of being forgotten
        // or wrongly reported as present.
        var managedUncs = previouslyManaged
            .Where(unc => !plan.ToRemove.Contains(unc, StringComparer.OrdinalIgnoreCase))
            .Concat(outcome.Added.Select(p => p.UncPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var addedNames = outcome.Added.Select(p => p.DisplayName).ToList();
        var failedNames = outcome.Failed.Select(f => f.Printer.DisplayName).ToList();

        return new SyncResult(managedUncs, addedNames, failedNames);
    }
}
