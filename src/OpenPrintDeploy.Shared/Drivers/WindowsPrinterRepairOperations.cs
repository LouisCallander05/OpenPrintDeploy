using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenPrintDeploy.Shared.Drivers;

/// <summary>Per-user repair only: never modifies server queues, jobs, or driver policy.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinterRepairOperations : IPrinterRepairOperations
{
    public void VerifyDriver(string unc, PreparedDriver driver)
    {
        var local = WindowsDriverStore.Find(driver.Name);
        var queue = WindowsDriverStore.ForLocalQueue(unc);
        if (!Matches(local, driver) || !Matches(queue, driver))
            throw new InvalidOperationException("The queue and prepared local driver must match before connection repair.");
    }

    private static bool Matches(InstalledDriver? actual, PreparedDriver expected)
        => actual is not null && actual.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase)
            && actual.Environment == expected.Environment && actual.Version == expected.Version && actual.Date == expected.Date;

    public bool SupportsDuplex(string unc) => WithPrinter(unc, false, handle =>
    {
        var port = WithInfo(handle, 2, ptr => Marshal.PtrToStructure<PrinterInfo2>(ptr).Port);
        return DeviceCapabilities(unc, port, 7, IntPtr.Zero, IntPtr.Zero) switch // DC_DUPLEX
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException("The driver could not report duplex capability; leaving the connection unchanged.")
        };
    });

    public bool ServerDefaultsToDuplex(string unc, PreparedDriver driver) => WithPrinter(unc, true, handle =>
    {
        // Bypass the local spooler cache. An unavailable server or changed
        // package must defer repair before any connection is disconnected.
        if (!Matches(WindowsDriverStore.ForPrinterHandle(handle), driver))
            throw new InvalidOperationException("The live server driver changed; retry after driver preparation.");
        var mode = ReadDevMode(handle, 8)
            ?? throw new InvalidOperationException("The server did not supply printer defaults.");
        return (ReadFields(mode) & 0x1000) != 0 && BinaryPrimitives.ReadInt16LittleEndian(mode.AsSpan(94)) is 2 or 3;
    });

    public bool HasPendingUserJobs(string unc) => WithPrinter(unc, false, handle =>
    {
        if (EnumJobs(handle, 0, 4096, 1, IntPtr.Zero, 0, out var needed, out _)) return false;
        var error = Marshal.GetLastWin32Error();
        if (error != 122) throw new Win32Exception(error, "Could not check pending print jobs.");
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumJobs(handle, 0, 4096, 1, buffer, needed, out _, out var count)) throw new Win32Exception();
            if (count >= 4096) return true; // Cannot prove the rest of the queue is idle.
            for (var i = 0; i < count; i++)
            {
                var job = Marshal.PtrToStructure<JobInfo1>(buffer + i * Marshal.SizeOf<JobInfo1>());
                var owner = job.UserName;
                if (string.IsNullOrWhiteSpace(owner)
                    || UserName(owner).Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    });

    private static string UserName(string value) => value[(value.LastIndexOf('\\') + 1)..].Split('@')[0];

    public PrinterRepairBackup Capture(string unc, PreparedDriver driver)
    {
        var defaultPrinter = ReadDefault();
        return WithPrinter(unc, false, handle =>
        {
            var canonicalName = WithInfo(handle, 2, ptr =>
                Marshal.PtrToStringUni(Marshal.PtrToStructure<PrinterInfo2>(ptr).Name));
            return new PrinterRepairBackup(unc, driver.Name, defaultPrinter, ReadDevMode(handle, 9))
            {
                WasDefault = defaultPrinter is not null &&
                    (defaultPrinter.Equals(unc, StringComparison.OrdinalIgnoreCase)
                    || defaultPrinter.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
            };
        });
    }

    public void Disconnect(string unc)
    {
        if (!DeletePrinterConnection(unc)) throw new Win32Exception();
    }

    public void Connect(string unc, PreparedDriver driver) => WindowsPrinterConnection.Add(unc, driver);

    public void RestorePreferences(string unc, PrinterRepairBackup backup)
    {
        if (backup.UserDevMode is null) return;
        WithPrinter(unc, false, handle =>
        {
            var size = DocumentProperties(IntPtr.Zero, handle, unc, IntPtr.Zero, IntPtr.Zero, 0);
            if (size < 220 || size > 131070) throw new InvalidOperationException("Invalid driver settings buffer size.");
            var mode = Marshal.AllocHGlobal(size);
            var info = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (DocumentProperties(IntPtr.Zero, handle, unc, mode, IntPtr.Zero, 2) != 1)
                    throw new InvalidOperationException("Could not read the reconnected driver's defaults.");
                var fresh = new byte[size];
                Marshal.Copy(mode, fresh, 0, size);
                MergePublicPreferences(backup.UserDevMode, fresh);
                Marshal.Copy(fresh, 0, mode, size);
                // The driver validates public settings against fresh device data.
                // Never reapply the old private DEVMODE/finishing configuration.
                if (DocumentProperties(IntPtr.Zero, handle, unc, mode, mode, 10) != 1)
                    throw new InvalidOperationException("The driver rejected restored printing preferences.");
                Marshal.WriteIntPtr(info, mode);
                if (!SetPrinter(handle, 9, info, 0)) throw new Win32Exception();
                return true;
            }
            finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(mode); }
        });
    }

    internal static void MergePublicPreferences(byte[] previous, byte[] fresh)
    {
        if (previous.Length < 220 || fresh.Length < 220) throw new InvalidDataException("Invalid saved printer settings.");
        var fields = ReadFields(previous) & ReadFields(fresh);
        // DEVMODEW public printer fields. Keep the fresh header, device name,
        // size/version, private driver bytes and unsupported fields unchanged.
        (uint Flag, int Offset, int Length)[] settings =
        [
            (0x1, 76, 2), (0x2, 78, 2), (0x4, 80, 2), (0x8, 82, 2),
            (0x10, 84, 2), (0x100, 86, 2), (0x200, 88, 2), (0x400, 90, 2),
            (0x800, 92, 2), (0x1000, 94, 2), (0x2000, 96, 2),
            (0x4000, 98, 2), (0x8000, 100, 2), (0x10000, 102, 64),
            (0x800000, 188, 4), (0x1000000, 192, 4),
            (0x2000000, 196, 4), (0x4000000, 200, 4)
        ];
        foreach (var (flag, offset, length) in settings)
            if ((fields & flag) != 0) previous.AsSpan(offset, length).CopyTo(fresh.AsSpan(offset, length));
    }

    public void RestoreDefault(PrinterRepairBackup backup)
    {
        // Windows can select a different default while the old one is absent.
        // Account for server queue names that differ from their share names.
        if (backup.WasDefault && backup.DefaultPrinter is not null
            && !SetDefaultPrinter(backup.DefaultPrinter)) throw new Win32Exception();
    }

    private static uint ReadFields(byte[] mode) => BinaryPrimitives.ReadUInt32LittleEndian(mode.AsSpan(72));

    private static byte[]? ReadDevMode(IntPtr handle, uint level) => WithInfo(handle, level, ptr =>
    {
        var mode = Marshal.ReadIntPtr(ptr);
        if (mode == IntPtr.Zero) return null;
        var size = (ushort)Marshal.ReadInt16(mode, 68);
        var extra = (ushort)Marshal.ReadInt16(mode, 70);
        if (size < 220) throw new InvalidDataException("Incomplete printer DEVMODE.");
        var bytes = new byte[size + extra];
        Marshal.Copy(mode, bytes, 0, bytes.Length);
        return bytes;
    });

    private static string? ReadDefault()
    {
        uint needed = 0;
        GetDefaultPrinter(null, ref needed);
        if (needed == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2) return null;
            throw new Win32Exception(error);
        }
        var name = new StringBuilder(checked((int)needed));
        if (!GetDefaultPrinter(name, ref needed)) throw new Win32Exception();
        return name.ToString();
    }

    private static T WithPrinter<T>(string unc, bool liveServer, Func<IntPtr, T> read)
    {
        var options = new PrinterOptions { Size = 8, Flags = liveServer ? 1u : 0u };
        if (!OpenPrinter2(unc, out var handle, IntPtr.Zero, ref options)) throw new Win32Exception();
        try { return read(handle); }
        finally { ClosePrinter(handle); }
    }

    private static T WithInfo<T>(IntPtr handle, uint level, Func<IntPtr, T> read)
    {
        GetPrinter(handle, level, IntPtr.Zero, 0, out var needed);
        if (needed == 0) throw new Win32Exception();
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!GetPrinter(handle, level, buffer, needed, out _)) throw new Win32Exception();
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterOptions { public uint Size, Flags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public IntPtr Server, Name, Share;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Port;
        public IntPtr Driver, Comment, Location, DevMode, Separator, Processor, DataType, Parameters, Security;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JobInfo1
    {
        public uint Id;
        public IntPtr PrinterName, MachineName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        public IntPtr Document, DataType, Status;
        public uint StatusFlags, Priority, Position, TotalPages, PagesPrinted;
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "OpenPrinter2W", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter2(string name, out IntPtr handle, IntPtr defaults, ref PrinterOptions options);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buffer, uint size, out uint needed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int DeviceCapabilities(string name, string? port, ushort capability, IntPtr output, IntPtr devmode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "EnumJobsW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumJobs(IntPtr handle, uint first, uint count, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "DeletePrinterConnectionW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeletePrinterConnection(string name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int DocumentProperties(IntPtr window, IntPtr handle, string name, IntPtr output, IntPtr input, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "SetPrinterW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPrinter(IntPtr handle, uint level, IntPtr info, uint command);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDefaultPrinter(StringBuilder? name, ref uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDefaultPrinter(string name);
}
