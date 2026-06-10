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
    private const uint PrinterEnumConnections = 0x00000004;

    public Task ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
    {
        foreach (var printer in plan.ToAdd)
        {
            ct.ThrowIfCancellationRequested();
            if (!AddPrinterConnection(printer.UncPath))
            {
                var code = Marshal.GetLastWin32Error();
                var reason = new Win32Exception(code).Message;
                throw new Win32Exception(code,
                    $"AddPrinterConnection failed for {printer.UncPath}: {reason}");
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

    public Task<IReadOnlyList<string>> EnumerateInstalledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        uint cbNeeded = 0;
        uint cReturned = 0;

        if (!EnumPrinters(PrinterEnumConnections, null, 2, IntPtr.Zero, 0, ref cbNeeded, ref cReturned))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 122) // ERROR_INSUFFICIENT_BUFFER
            {
                throw new Win32Exception(error, "EnumPrinters failed while probing printer connections.");
            }
        }

        if (cbNeeded == 0)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var buffer = Marshal.AllocHGlobal((int)cbNeeded);
        try
        {
            if (!EnumPrinters(PrinterEnumConnections, null, 2, buffer, cbNeeded, ref cbNeeded, ref cReturned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumPrinters failed while enumerating printer connections.");
            }

            var result = new List<string>();
            var size = Marshal.SizeOf<PrinterInfo2>();
            for (var i = 0; i < cReturned; i++)
            {
                var ptr = IntPtr.Add(buffer, i * size);
                var info = Marshal.PtrToStructure<PrinterInfo2>(ptr);
                if (!string.IsNullOrWhiteSpace(info.pServerName) && !string.IsNullOrWhiteSpace(info.pShareName))
                {
                    result.Add($"\\\\{info.pServerName}\\{info.pShareName}");
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(result.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddPrinterConnection(string pName);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, ref uint pcbNeeded, ref uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeletePrinterConnection(string pName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pServerName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pPrinterName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pShareName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pPortName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pDriverName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pComment;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pLocation;
        public IntPtr pDevMode;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pSepFile;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pPrintProcessor;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pDatatype;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }
}
