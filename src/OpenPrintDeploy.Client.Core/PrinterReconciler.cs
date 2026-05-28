using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

/// <summary>What the applier must do to converge on the server's resolved set.</summary>
public sealed record ReconcileResult(
    IReadOnlyList<PrinterDto> ToAdd,
    IReadOnlyList<string> ToRemove);

/// <summary>
/// Pure diff between the server's desired printer set and the set this client
/// installed last time. Only printers we previously deployed are eligible for
/// removal, so a user's own manually-added printers are never touched.
/// UNC comparison is case-insensitive, matching Windows printer connections.
/// </summary>
public static class PrinterReconciler
{
    public static ReconcileResult Reconcile(
        SyncResponseDto desired,
        IReadOnlyCollection<string> previouslyManaged)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(previouslyManaged);

        var desiredUncs = desired.Printers
            .Select(p => p.UncPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var managed = previouslyManaged.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = desired.Printers
            .Where(p => !managed.Contains(p.UncPath))
            .ToList();

        var toRemove = managed
            .Where(unc => !desiredUncs.Contains(unc))
            .ToList();

        return new ReconcileResult(toAdd, toRemove);
    }
}
