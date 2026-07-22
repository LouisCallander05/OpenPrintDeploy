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
///
/// "Already installed" is matched on the connection's UNC <em>or</em> on
/// <c>\\host\&lt;printer name&gt;</c>: you connect to a printer by its SHARE name,
/// but Windows enumerates the resulting connection by the server's PRINTER name,
/// and the two differ when an admin named the queue differently from its share.
/// Without the second form, such a printer never matches what's installed and is
/// re-added on every sync. The printer name is the DisplayName captured at import.
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
        // should have no printers" from "I don't know right now", so we don't
        // reconcile zone assignments. Explicit global removals are independent
        // of directory resolution and remain safe to enforce.
        var forcedRemovals = (desired.RemovePrinters ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!desired.Authoritative)
        {
            return new ReconcileResult([], forcedRemovals, []);
        }

        var desiredUncs = desired.Printers
            .Select(p => p.UncPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var managed = previouslyManaged.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = currentlyInstalled.ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsInstalled(PrinterDto p)
            => installed.Contains(p.UncPath)
               || (NameUnc(p) is { } nameUnc && installed.Contains(nameUnc));

        var toAdd = desired.Printers
            .Where(p => !IsInstalled(p))
            .ToList();

        var toRemove = managed
            .Where(unc => !desiredUncs.Contains(unc))
            .Concat(forcedRemovals)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Adopt: a desired printer already on the machine that we don't yet
        // track. Claim it without re-adding (it's already installed, so it's not
        // in toAdd) — the reconcile loop then owns it like any other managed
        // printer. Recorded by its UNC (the share form we'd reinstall with).
        var toAdopt = desired.Printers
            .Where(p => IsInstalled(p) && !managed.Contains(p.UncPath))
            .Select(p => p.UncPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReconcileResult(toAdd, toRemove, toAdopt);
    }

    /// <summary>
    /// <c>\\host\&lt;DisplayName&gt;</c> built from a printer's UNC host and its
    /// display (= server printer) name — the form Windows enumerates a connection
    /// under. Null when there's no usable host or name.
    /// </summary>
    private static string? NameUnc(PrinterDto p)
    {
        var name = p.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || !p.UncPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = p.UncPath[2..];
        var slash = rest.IndexOf('\\');
        return slash <= 0 ? null : $@"\\{rest[..slash]}\{name}";
    }
}
