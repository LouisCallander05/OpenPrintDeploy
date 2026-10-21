using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Applies a <see cref="ReconcileResult"/> in the user's session: adds/removes
/// per-user printer connections and sets the default. The concrete
/// implementation is Windows-only (Add-Printer / Remove-Printer) and lives in
/// the tray app; this interface keeps the orchestration testable cross-platform.
/// </summary>
public interface IPrinterApplier
{
    /// <summary>
    /// Applies the plan best-effort: each add is independent, so one failing
    /// printer (a name clash with an orphaned printer, an unreachable server, a
    /// point-and-print block) never stops the others. Returns what actually
    /// happened so the caller can persist only the printers that installed and
    /// surface the ones that didn't.
    /// </summary>
    Task<ApplyOutcome> ApplyAsync(ReconcileResult plan, CancellationToken ct = default);

    Task<IReadOnlyList<string>> EnumerateInstalledAsync(CancellationToken ct = default);
}

/// <summary>The result of applying a plan: the adds that succeeded and the ones that failed.</summary>
public sealed record ApplyOutcome
{
    public ApplyOutcome(
        IReadOnlyList<PrinterDto> added,
        IReadOnlyList<PrinterApplyError> failed)
        : this(added, failed, [], [])
    {
    }

    public ApplyOutcome(
        IReadOnlyList<PrinterDto> added,
        IReadOnlyList<PrinterApplyError> failed,
        IReadOnlyList<PrinterRemovalResult> removed,
        IReadOnlyList<PrinterRemovalError> failedRemovals)
    {
        Added = added;
        Failed = failed;
        Removed = removed;
        FailedRemovals = failedRemovals;
    }

    public IReadOnlyList<PrinterDto> Added { get; }
    public IReadOnlyList<PrinterApplyError> Failed { get; }
    public IReadOnlyList<PrinterRemovalResult> Removed { get; }
    public IReadOnlyList<PrinterRemovalError> FailedRemovals { get; }

    public static ApplyOutcome Empty { get; } = new([], [], [], []);
}

/// <summary>A single printer that failed to install, with the OS reason.</summary>
public sealed record PrinterApplyError(PrinterDto Printer, string Message);

public sealed record PrinterRemovalResult(string UncPath, bool AlreadyAbsent = false);

public sealed record PrinterRemovalError(string UncPath, string Message);
