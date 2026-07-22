using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Sync;

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

    private readonly IReadOnlyCollection<string> _allowedPrintServers;

    /// <param name="allowedPrintServers">
    /// Hosts whose printers may be installed. Empty enforces only UNC-format
    /// validation (no host allow-listing). The tray defaults this to the
    /// configured server's host; an admin can widen it for split print servers.
    /// </param>
    public WindowsPrinterApplier(IReadOnlyCollection<string>? allowedPrintServers = null)
        => _allowedPrintServers = allowedPrintServers ?? [];

    public Task<ApplyOutcome> ApplyAsync(ReconcileResult plan, CancellationToken ct = default)
    {
        var added = new List<PrinterDto>();
        var failed = new List<PrinterApplyError>();
        var removed = new List<PrinterRemovalResult>();
        var failedRemovals = new List<PrinterRemovalError>();

        foreach (var printer in plan.ToAdd)
        {
            ct.ThrowIfCancellationRequested();

            // Defence in depth: never hand the spooler a malformed UNC or a host
            // outside the allow-list, even though the server is TLS-pinned — a
            // compromised server must not be able to redirect the spooler.
            if (!PrinterUncPolicy.IsAllowed(printer.UncPath, _allowedPrintServers, out var blockReason))
            {
                failed.Add(new PrinterApplyError(printer, $"blocked: {blockReason}"));
                continue;
            }

            // Best-effort per printer: a single failed add (a name clash with an
            // orphaned printer, an unreachable server, a point-and-print block)
            // must not stop the rest of the set from installing.
            if (AddPrinterConnection(printer.UncPath))
            {
                added.Add(printer);
            }
            else
            {
                var reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                failed.Add(new PrinterApplyError(printer, reason));
            }
        }

        foreach (var unc in plan.ToRemove)
        {
            ct.ThrowIfCancellationRequested();
            if (DeletePrinterConnection(unc))
            {
                removed.Add(new PrinterRemovalResult(unc));
                continue;
            }

            var error = Marshal.GetLastWin32Error();
            // A stale managed-state entry is already converged and should be
            // reported as such, not as a removal failure requiring attention.
            if (error is 2 or 1801) // ERROR_FILE_NOT_FOUND / ERROR_INVALID_PRINTER_NAME
            {
                removed.Add(new PrinterRemovalResult(unc, AlreadyAbsent: true));
            }
            else
            {
                failedRemovals.Add(new PrinterRemovalError(
                    unc, new Win32Exception(error).Message));
            }
        }

        return Task.FromResult(new ApplyOutcome(added, failed, removed, failedRemovals));
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

                // For a per-user printer CONNECTION (which is all this app creates),
                // the full UNC is carried in pPrinterName — e.g. "\\srv\share".
                // pServerName/pShareName are typically empty for connections, so
                // reconstructing the UNC from them misses every printer and makes
                // the reconciler re-add the whole set on every sync. Match the exact
                // string we passed to AddPrinterConnection instead.
                if (!string.IsNullOrWhiteSpace(info.pPrinterName))
                {
                    result.Add(info.pPrinterName.Trim());
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
