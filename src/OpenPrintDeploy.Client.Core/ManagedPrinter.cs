namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Where a managed printer came from. Decides leave-no-trace behaviour on
/// <em>uninstall</em>: a printer OPD itself created is OPD's to remove, whereas
/// one OPD merely adopted (it pre-existed — e.g. left by PaperCut Print Deploy)
/// should be handed back untouched, the way OPD found the machine.
/// Note this does NOT change day-to-day reconcile removal: a printer dropped
/// from the user's zone is removed regardless of origin.
/// </summary>
public enum PrinterOrigin
{
    /// <summary>OPD added this printer connection itself.</summary>
    Created,

    /// <summary>The connection already existed; OPD claimed it on a later sync.</summary>
    Adopted,
}

/// <summary>
/// A printer connection OPD manages, with its provenance. Persisted by the
/// client so the reconcile loop knows what it may remove and a future uninstall
/// can distinguish created (remove) from adopted (keep). Matched by
/// <paramref name="Unc"/> only — never by display name — so OPD can never seize
/// a user's personal printer that happens to share a name.
/// </summary>
/// <param name="Unc">The printer connection UNC, e.g. <c>\\printsrv\Library-BW</c>.</param>
/// <param name="Origin">How OPD came to manage it.</param>
public sealed record ManagedPrinter(string Unc, PrinterOrigin Origin);
