using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

/// <summary>What the applier must do to converge on the server's resolved set.</summary>
/// <param name="ToAdd">Desired printers not yet installed — the applier adds these.</param>
/// <param name="ToRemove">Managed printers no longer desired — the applier removes these.</param>
/// <param name="ToAdopt">
/// Desired printers already present on the machine that OPD does not yet track
/// (typically a connection a prior tool such as PaperCut Print Deploy left to
/// the same UNC). The applier does nothing with these — adoption is pure
/// metadata; the caller records them as managed so the reconcile loop owns their
/// lifecycle from now on. No re-add means no flicker and no default-printer reset.
/// </param>
public sealed record ReconcileResult(
    IReadOnlyList<PrinterDto> ToAdd,
    IReadOnlyList<string> ToRemove,
    IReadOnlyList<string> ToAdopt);

/// <summary>
/// Pure diff between the server's desired printer set and the set this client
/// installed last time. Only printers we previously deployed are eligible for
/// removal, so a user's own manually-added printers are never touched.
/// UNC comparison is case-insensitive, matching Windows printer connections.
/// A non-authoritative response (directory/server unavailable) yields an empty
/// plan — printers are never removed on a state the server couldn't confirm.
/// </summary>
public static class PrinterReconciler
{
    public static ReconcileResult Reconcile(
        SyncResponseDto desired,
        IReadOnlyCollection<string> previouslyManaged,
        IReadOnlyCollection<string> currentlyInstalled)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(previouslyManaged);
        ArgumentNullException.ThrowIfNull(currentlyInstalled);

        // A non-authoritative response means the server could not resolve the
        // user (directory/DC outage, empty/garbled body). We can't tell "you
        // should have no printers" from "I don't know right now", so we make NO
        // changes — never tear down printers we can't confirm are unwanted.
        // Printers re-converge on the next sync once the directory recovers.
        if (!desired.Authoritative)
        {
            return new ReconcileResult([], [], []);
        }

        var desiredUncs = desired.Printers
            .Select(p => p.UncPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var managed = previouslyManaged.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = currentlyInstalled.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = desired.Printers
            .Where(p => !installed.Contains(p.UncPath))
            .ToList();

        var toRemove = managed
            .Where(unc => !desiredUncs.Contains(unc))
            .ToList();

        // Adopt: a desired printer already on the machine that we don't yet
        // track. Claim it without re-adding (it's already installed, so it's not
        // in toAdd) — the reconcile loop then owns it like any other managed
        // printer. Match by UNC, the connection's true identity.
        var toAdopt = desiredUncs
            .Where(unc => installed.Contains(unc) && !managed.Contains(unc))
            .ToList();

        return new ReconcileResult(toAdd, toRemove, toAdopt);
    }
}
