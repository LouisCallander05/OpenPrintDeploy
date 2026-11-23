using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenPrintDeploy.Shared.Drivers;

/// <summary>Reads the actual published queue, without GetPrinterDriver's client-side Type 4 projection.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsServerQueueDriver
{
    public static InstalledDriver Get(string unc)
        => GetCore(unc, Enumerate, WindowsDriverStore.Find);

    internal static InstalledDriver GetCore(string unc, Func<string, IReadOnlyList<(string Share, string Driver)>> queues,
        Func<string, string?, InstalledDriver?> drivers)
    {
        var parts = unc.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!unc.StartsWith(@"\\", StringComparison.Ordinal) || parts.Length != 2
            || parts.Any(p => p is "." or ".." || p.Contains('/') || p.Contains(':')))
            throw new InvalidDataException("Invalid shared printer path.");
        var server = @"\\" + parts[0];
        var matches = queues(server).Where(q => q.Share.Equals(parts[1], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].Driver))
            throw new InvalidOperationException("The live server did not identify exactly one matching shared queue; leaving the connection unchanged.");
        return drivers(matches[0].Driver, server)
            ?? throw new InvalidOperationException("The live shared queue's driver is unavailable; retry after driver preparation.");
    }

    private static IReadOnlyList<(string Share, string Driver)> Enumerate(string server)
    {
        const uint byName = 8; // PRINTER_ENUM_NAME: query the server, not local connections.
        var ok = EnumPrinters(byName, server, 2, IntPtr.Zero, 0, out var needed, out _);
        if (ok && needed == 0) return [];
        if (!ok && Marshal.GetLastWin32Error() != 122) throw new Win32Exception();
        if (needed == 0) throw new InvalidOperationException("The server returned no queue metadata.");
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(byName, server, 2, buffer, needed, out _, out var count)) throw new Win32Exception();
            var result = new List<(string Share, string Driver)>();
            for (var i = 0; i < count; i++)
            {
                var queue = Marshal.PtrToStructure<PrinterInfo2>(buffer + i * Marshal.SizeOf<PrinterInfo2>());
                var share = Marshal.PtrToStringUni(queue.Share);
                var driver = Marshal.PtrToStringUni(queue.Driver);
                if (!string.IsNullOrWhiteSpace(share) && !string.IsNullOrWhiteSpace(driver)) result.Add((share, driver));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr Server, Name, Share, Port, Driver, Comment, Location, DevMode, Separator, Processor, DataType, Parameters, Security;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string name, uint level, IntPtr buffer, uint size,
        out uint needed, out uint count);
}
