using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenPrintDeploy.Shared.Drivers;

/// <summary>Creates only the current user's connection, using a service-prepared local driver when available.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrinterConnection
{
    public static void Add(string unc, PreparedDriver? prepared)
    {
        // Background sync must never ask a standard user for administrator credentials.
        var info = new ConnectionInfo { Flags = 0x40 }; // PRINTER_CONNECTION_NO_UI
        if (prepared is not null)
        {
            var installed = WindowsDriverStore.Find(prepared.Name);
            if (installed is null || installed.Environment != prepared.Environment
                || installed.Version != prepared.Version || installed.Date != prepared.Date)
                throw new InvalidOperationException("The prepared local driver changed or is missing; retry sync to prepare it again.");

            // PRINTER_CONNECTION_MISMATCH selects this installed driver for local rendering.
            // The service, rather than Point and Print, owns package/version updates.
            info.Flags |= 0x20;
            info.DriverName = installed.Name;
        }

        if (!AddPrinterConnection2(IntPtr.Zero, unc, 1, ref info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Printer connection failed (Win32 {error}, 0x{error:X8}): "
                + new Win32Exception(error).Message
                + (prepared is null ? " No service-prepared local driver was supplied." : $" Local driver: {prepared.Name}.")
                + " Interactive driver installation is disabled during sync.");
        }
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
