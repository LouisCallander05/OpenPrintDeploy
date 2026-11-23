using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenPrintDeploy.Shared.Drivers;

/// <summary>Creates the current user's shared-printer connection after verifying any service-prepared driver.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrinterConnection
{
    public static void Add(string unc, PreparedDriver? prepared)
        => AddCore(prepared, WindowsDriverStore.Find, (flags, name) =>
        {
            var info = new ConnectionInfo { Flags = flags, DriverName = name };
            return AddPrinterConnection2(IntPtr.Zero, unc, 1, ref info) ? 0 : Marshal.GetLastWin32Error();
        });

    internal static void AddCore(PreparedDriver? prepared, Func<string, InstalledDriver?> findDriver,
        Func<uint, string?, int> connect)
    {
        // Background sync must never ask a standard user for administrator credentials.
        const uint noUi = 0x40; // PRINTER_CONNECTION_NO_UI
        if (prepared is not null)
        {
            var installed = findDriver(prepared.Name);
            if (installed is null || installed.Environment != prepared.Environment
                || installed.Version != prepared.Version || installed.Date != prepared.Date)
                throw new InvalidOperationException("The prepared local driver changed or is missing; retry sync to prepare it again.");
        }

        // The service installs the server's driver under SYSTEM; the user still
        // needs a normal shared-queue connection, including its device options.
        // MISMATCH is for substituting a different local rendering driver. Using
        // it for an identical prepared driver can break vendor finishing/duplex
        // settings even though Windows reports that the connection succeeded.
        var error = connect(noUi, null);
        if (error != 0)
        {
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
