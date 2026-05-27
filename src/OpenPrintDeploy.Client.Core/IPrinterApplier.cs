namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Applies a <see cref="ReconcileResult"/> in the user's session: adds/removes
/// per-user printer connections and sets the default. The concrete
/// implementation is Windows-only (Add-Printer / Remove-Printer) and lives in
/// the tray app; this interface keeps the orchestration testable cross-platform.
/// </summary>
public interface IPrinterApplier
{
    Task ApplyAsync(ReconcileResult plan, CancellationToken ct = default);
}
