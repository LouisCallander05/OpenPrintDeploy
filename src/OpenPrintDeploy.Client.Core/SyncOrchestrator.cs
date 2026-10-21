namespace OpenPrintDeploy.Client.Core;

/// <param name="ManagedPrinters">All printers this client now manages, each with its provenance.</param>
/// <param name="AddedNames">Display names of printers added in this cycle (empty when nothing changed).</param>
/// <param name="FailedNames">Display names of printers that failed to install this cycle (best-effort apply).</param>
public sealed record SyncResult(
    IReadOnlyList<ManagedPrinter> ManagedPrinters,
    IReadOnlyList<string> AddedNames,
    IReadOnlyList<string> FailedNames)
{
    public IReadOnlyList<PrinterApplyError> Failures { get; init; } = [];
}

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
    private readonly IDriverPreparer? _drivers;

    public SyncOrchestrator(SyncApiClient api, IPrinterApplier applier, IDriverPreparer? drivers = null)
    {
        _api = api;
        _applier = applier;
        _drivers = drivers;
    }

    public async Task<SyncResult> SyncOnceAsync(
        string? machineName,
        IReadOnlyCollection<ManagedPrinter> previouslyManaged,
        CancellationToken ct = default)
    {
        var desired = await _api.FetchAsync(machineName, ct);
        var currentlyInstalled = await _applier.EnumerateInstalledAsync(ct);
        var plan = PrinterReconciler.Reconcile(
            desired,
            previouslyManaged.Select(m => m.Unc).ToList(),
            currentlyInstalled);
        // Check all assigned queues, including existing connections, for updates.
        // Failed preparation must not cause working connections to be removed.
        var driverFailures = _drivers is not null && desired.Authoritative
            ? await _drivers.PrepareAsync(desired.Printers.Where(p =>
                !plan.ToRemove.Contains(p.UncPath, StringComparer.OrdinalIgnoreCase)).ToList(), ct)
            : [];
        var blocked = driverFailures.Select(f => f.Printer.UncPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan = plan with { ToAdd = plan.ToAdd.Where(p => !blocked.Contains(p.UncPath)
            && !plan.ToRemove.Contains(p.UncPath, StringComparer.OrdinalIgnoreCase)).ToList() };
        var outcome = await _applier.ApplyAsync(plan, ct);

        // Rebuild the managed set, preserving each printer's provenance. The
        // `seen` set both de-duplicates and enforces precedence — the first
        // origin recorded for a UNC wins:
        //  - carried over: still-desired managed printers keep their origin
        //    (a desired-but-missing printer is in both previouslyManaged and
        //     ToAdd, and this precedence keeps its original origin, not Created);
        //  - created: printers that actually installed this cycle are ours. A
        //    failed add isn't claimed, so the next sync retries it instead of
        //    forgetting it or wrongly reporting it present;
        //  - adopted: printers that already existed and we now claim (e.g. left
        //    by PaperCut) — kept on uninstall, removed only via the zone.
        var managed = new List<ManagedPrinter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in previouslyManaged)
        {
            if (plan.ToRemove.Contains(m.Unc, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(m.Unc))
            {
                managed.Add(m);
            }
        }

        foreach (var p in outcome.Added)
        {
            if (seen.Add(p.UncPath))
            {
                managed.Add(new ManagedPrinter(p.UncPath, PrinterOrigin.Created));
            }
        }

        foreach (var unc in plan.ToAdopt)
        {
            if (seen.Add(unc))
            {
                managed.Add(new ManagedPrinter(unc, PrinterOrigin.Adopted));
            }
        }

        var addedNames = outcome.Added.Select(p => p.DisplayName).ToList();
        var failedNames = outcome.Failed.Concat(driverFailures).Select(f => f.Printer.DisplayName).Distinct().ToList();

        return new SyncResult(managed, addedNames, failedNames) { Failures = outcome.Failed.Concat(driverFailures).ToList() };
    }
}
