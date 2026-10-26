using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenPrintDeploy.Shared.Drivers;

/// <summary>Creates only the current user's connection, using a service-prepared local driver when available.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrinterConnection
{
    /// <returns>True when Windows' standard silent selection was needed after error 3012.</returns>
    public static bool Add(string unc, PreparedDriver? prepared)
        => AddCore(prepared, WindowsDriverStore.Find, (flags, name) =>
        {
            var info = new ConnectionInfo { Flags = flags, DriverName = name };
            return AddPrinterConnection2(IntPtr.Zero, unc, 1, ref info) ? 0 : Marshal.GetLastWin32Error();
        });

    internal static bool AddCore(PreparedDriver? prepared, Func<string, InstalledDriver?> findDriver,
        Func<uint, string?, int> connect)
    {
        // Background sync must never ask a standard user for administrator credentials.
        const uint noUi = 0x40; // PRINTER_CONNECTION_NO_UI
        const uint mismatch = 0x20; // PRINTER_CONNECTION_MISMATCH
        uint flags = noUi;
        string? driverName = null;
        if (prepared is not null)
        {
            var installed = findDriver(prepared.Name);
            if (installed is null || installed.Environment != prepared.Environment
                || installed.Version != prepared.Version || installed.Date != prepared.Date)
                throw new InvalidOperationException("The prepared local driver changed or is missing; retry sync to prepare it again.");

            // PRINTER_CONNECTION_MISMATCH selects this installed driver for local rendering.
            // The service, rather than Point and Print, owns package/version updates.
            flags |= mismatch;
            driverName = installed.Name;
        }

        var error = connect(flags, driverName);
        if (error == 0) return false;

        // Some queues reject explicit local-driver binding with ERROR_PRINTER_NOT_FOUND
        // even though the same user can connect normally using the installed driver.
        // Try normal selection once, retaining NO_UI and all Windows policy checks.
        // Never fall back to an interactive AddPrinterConnection call or elevation.
        var usedFallback = prepared is not null && error == 3012;
        if (usedFallback) error = connect(noUi, null);

        if (error != 0)
        {
            throw new Win32Exception(error, $"Printer connection failed (Win32 {error}, 0x{error:X8}): "
                + new Win32Exception(error).Message
                + (prepared is null ? " No service-prepared local driver was supplied." : $" Local driver: {prepared.Name}.")
                + (usedFallback ? " Explicit driver selection returned Win32 3012; standard silent selection also failed." : "")
                + " Interactive driver installation is disabled during sync.");
        }
        return usedFallback;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectionInfo
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? DriverName;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "AddPrinterConnection2W", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddPrinterConnection2(IntPtr window, string name, uint level, ref ConnectionInfo info);
}
