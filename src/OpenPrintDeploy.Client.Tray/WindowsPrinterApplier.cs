using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Applies the reconciled printer set in the user's session via the Win32
/// spooler. <c>AddPrinterConnection</c>/<c>DeletePrinterConnection</c> write to
/// the per-user (HKCU) connection list, which is why this must run in the user
/// context (the tray) rather than the SYSTEM service.
/// </summary>
public sealed class WindowsPrinterApplier : IPrinterApplier
{
    public Task ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
    {
        foreach (var printer in plan.ToAdd)
        {
            ct.ThrowIfCancellationRequested();
            if (!AddPrinterConnection(printer.UncPath))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"AddPrinterConnection failed for {printer.UncPath}");
            }
        }

        foreach (var unc in plan.ToRemove)
        {
            ct.ThrowIfCancellationRequested();
            // Best-effort: a printer the user already removed isn't an error.
            DeletePrinterConnection(unc);
        }

        return Task.CompletedTask;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddPrinterConnection(string pName);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeletePrinterConnection(string pName);
}
