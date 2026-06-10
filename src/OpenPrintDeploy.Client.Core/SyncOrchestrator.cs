namespace OpenPrintDeploy.Client.Core;

/// <param name="ManagedUncs">UNC paths of all printers this client now manages.</param>
/// <param name="AddedNames">Display names of printers added in this cycle (empty when nothing changed).</param>
public sealed record SyncResult(IReadOnlyList<string> ManagedUncs, IReadOnlyList<string> AddedNames);

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
        await _applier.ApplyAsync(plan, ct);

        var managedUncs = previouslyManaged
            .Where(unc => !plan.ToRemove.Contains(unc, StringComparer.OrdinalIgnoreCase))
            .Concat(plan.ToAdd.Select(p => p.UncPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var addedNames = plan.ToAdd.Select(p => p.DisplayName).ToList();

        return new SyncResult(managedUncs, addedNames);
    }
}
