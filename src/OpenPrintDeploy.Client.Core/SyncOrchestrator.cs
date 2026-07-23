using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

/// <param name="ManagedPrinters">All printers this client now manages, each with its provenance.</param>
/// <param name="AddedNames">Display names of printers added in this cycle (empty when nothing changed).</param>
/// <param name="FailedNames">Display names of printers that failed to install this cycle (best-effort apply).</param>
public sealed record SyncResult(
    IReadOnlyList<ManagedPrinter> ManagedPrinters,
    IReadOnlyList<string> AddedNames,
    IReadOnlyList<string> FailedNames,
    SyncReportStatus ReportStatus = SyncReportStatus.Synced);

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
        IReadOnlyCollection<ManagedPrinter> previouslyManaged,
        CancellationToken ct = default,
        string? clientVersion = null,
        string? deviceId = null)
    {
        var requestedSyncId = Guid.NewGuid();
        SyncResponseDto? desired = null;

        try
        {
            desired = await _api.FetchAsync(
                machineName,
                ct,
                requestedSyncId,
                clientVersion,
                deviceId);
            var currentlyInstalled = await _applier.EnumerateInstalledAsync(ct);
            var plan = PrinterReconciler.Reconcile(
                desired,
                previouslyManaged.Select(m => m.Unc).ToList(),
                currentlyInstalled);
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
            var failedRemovals = outcome.FailedRemovals
                .Select(r => r.UncPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in previouslyManaged)
            {
                if (plan.ToRemove.Contains(m.Unc, StringComparer.OrdinalIgnoreCase)
                    && !failedRemovals.Contains(m.Unc))
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
            var failedNames = outcome.Failed.Select(f => f.Printer.DisplayName).ToList();
            var printerResults = BuildPrinterResults(desired, plan, outcome);
            var hasFailures = printerResults.Any(p => !p.Succeeded);
            var reportStatus = hasFailures
                ? SyncReportStatus.Partial
                : desired.Authoritative ? SyncReportStatus.Synced : SyncReportStatus.Deferred;

            if (desired.SyncId is { } reportId)
            {
                await TryReportAsync(new SyncReportDto(
                    reportId,
                    machineName,
                    clientVersion,
                    reportStatus,
                    printerResults,
                    DeviceId: deviceId), ct);
            }

            return new SyncResult(managed, addedNames, failedNames, reportStatus);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (desired?.SyncId is { } reportId)
            {
                await TryReportAsync(new SyncReportDto(
                    reportId,
                    machineName,
                    clientVersion,
                    SyncReportStatus.Failed,
                    [],
                    ex.Message,
                    deviceId), ct);
            }

            throw;
        }
    }

    private async Task TryReportAsync(SyncReportDto report, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await _api.ReportAsync(report, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The operational report has a deliberately short deadline. The
            // printer work is already complete, so a slow dashboard endpoint
            // must not hold up the user's tray sync.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reporting is observability only. It must never turn an otherwise
            // successful printer reconciliation into a failed client sync.
        }
    }

    private static IReadOnlyList<PrinterSyncResultDto> BuildPrinterResults(
        SyncResponseDto desired,
        ReconcileResult plan,
        ApplyOutcome outcome)
    {
        var results = new List<PrinterSyncResultDto>();
        var added = outcome.Added.ToDictionary(p => p.UncPath, StringComparer.OrdinalIgnoreCase);
        var addFailures = outcome.Failed.ToDictionary(p => p.Printer.UncPath, StringComparer.OrdinalIgnoreCase);
        var adopted = plan.ToAdopt.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attemptedAdds = plan.ToAdd.Select(p => p.UncPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var printer in desired.Printers)
        {
            if (addFailures.TryGetValue(printer.UncPath, out var failure))
            {
                results.Add(new PrinterSyncResultDto(
                    printer.DisplayName, printer.UncPath, PrinterSyncOperation.Installed,
                    Succeeded: false, Error: failure.Message));
            }
            else if (added.ContainsKey(printer.UncPath))
            {
                results.Add(new PrinterSyncResultDto(
                    printer.DisplayName, printer.UncPath, PrinterSyncOperation.Installed,
                    Succeeded: true));
            }
            else if (adopted.Contains(printer.UncPath))
            {
                results.Add(new PrinterSyncResultDto(
                    printer.DisplayName, printer.UncPath, PrinterSyncOperation.Adopted,
                    Succeeded: true));
            }
            else if (!attemptedAdds.Contains(printer.UncPath))
            {
                results.Add(new PrinterSyncResultDto(
                    printer.DisplayName, printer.UncPath, PrinterSyncOperation.Present,
                    Succeeded: true));
            }
        }

        var forced = (desired.RemovePrinters ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removalFailures = outcome.FailedRemovals.ToDictionary(r => r.UncPath, StringComparer.OrdinalIgnoreCase);
        var removals = outcome.Removed.ToDictionary(r => r.UncPath, StringComparer.OrdinalIgnoreCase);
        foreach (var unc in plan.ToRemove)
        {
            var reason = forced.Contains(unc)
                ? PrinterRemovalReason.GlobalRemoval
                : PrinterRemovalReason.NoLongerAssigned;
            if (removalFailures.TryGetValue(unc, out var failure))
            {
                results.Add(new PrinterSyncResultDto(
                    DisplayName: null, unc, PrinterSyncOperation.Removed,
                    Succeeded: false, Error: failure.Message, RemovalReason: reason));
            }
            else if (removals.TryGetValue(unc, out var removal))
            {
                results.Add(new PrinterSyncResultDto(
                    DisplayName: null, unc, PrinterSyncOperation.Removed,
                    Succeeded: true, RemovalReason: reason, AlreadyAbsent: removal.AlreadyAbsent));
            }
        }

        return results;
    }
}
