using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Removes per-user printer connections via the Win32 spooler. Mirrors the
/// tray's <c>WindowsPrinterApplier</c> deletion: <c>DeletePrinterConnection</c>
/// writes to the calling user's (HKCU) connection list, which is exactly why the
/// removal must run in each user's own context — not as SYSTEM.
/// </summary>
internal static class PrinterConnections
{
    /// <summary>
    /// Deletes a printer connection for the current user. Returns true if the
    /// spooler reported a removal; false (with a logged reason) when the
    /// connection wasn't present or the spooler refused — neither is fatal, the
    /// goal is "this UNC is gone for this user" and an already-absent printer
    /// satisfies that.
    /// </summary>
    public static bool TryRemove(string unc)
    {
        if (DeletePrinterConnection(unc))
        {
            CleanupLog.Info($"  Removed connection {unc}");
            return true;
        }

        var err = Marshal.GetLastWin32Error();
        // ERROR_INVALID_PRINTER_NAME (1801) / ERROR_UNKNOWN_PRINTER — already gone.
        var reason = new Win32Exception(err).Message;
        CleanupLog.Info($"  Not removed {unc} (likely already absent): {reason}");
        return false;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DeletePrinterConnection(string pName);
}
