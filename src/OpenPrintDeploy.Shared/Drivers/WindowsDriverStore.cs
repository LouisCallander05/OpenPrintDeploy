using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenPrintDeploy.Shared.Drivers;

public sealed record InstalledDriver(string Name, string Environment, ulong Version, long Date, string InfPath);

/// <summary>Package-only Windows installation: no vendor executables or legacy copy fallback.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsDriverStore
{
    private const string DriverEnvironment = "Windows x64";

    public static InstalledDriver ForLocalQueue(string queue)
    {
        if (!OpenPrinter(queue, out var handle, IntPtr.Zero)) throw new Win32Exception();
        try
        {
            GetPrinterDriver(handle, DriverEnvironment, 8, IntPtr.Zero, 0, out var needed);
            if (needed == 0) throw new Win32Exception();
            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetPrinterDriver(handle, DriverEnvironment, 8, buffer, needed, out _)) throw new Win32Exception();
                return Read(Marshal.PtrToStructure<DriverInfo8>(buffer));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(handle); }
    }

    public static InstalledDriver? Find(string name)
    {
        EnumPrinterDrivers(null, DriverEnvironment, 8, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
            return null;
        }
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinterDrivers(null, DriverEnvironment, 8, buffer, needed, out _, out var count))
                throw new Win32Exception();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<DriverInfo8>(buffer + i * Marshal.SizeOf<DriverInfo8>());
                if (string.Equals(Marshal.PtrToStringUni(info.Name), name, StringComparison.OrdinalIgnoreCase))
                    return Read(info);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string PublishedInf(string infPath)
    {
        var path = new StringBuilder(32768);
        if (!SetupGetInfPublishedName(infPath, path, (uint)path.Capacity, out _)) throw new Win32Exception();
        return path.ToString();
    }

    public static void Install(string infPath, string name)
    {
        var destination = new StringBuilder(32768);
        var size = (uint)destination.Capacity;
        // UPDP_SILENT_UPLOAD. Windows rejects unsigned/altered packages here.
        Marshal.ThrowExceptionForHR(UploadPrinterDriverPackage(null, infPath, DriverEnvironment, 1,
            IntPtr.Zero, destination, ref size));
        Marshal.ThrowExceptionForHR(InstallPrinterDriverFromPackage(null, destination.ToString(), name,
            DriverEnvironment, 1)); // IPDFP_COPY_ALL_FILES: also refresh an existing driver.
    }

    private static InstalledDriver Read(DriverInfo8 info) => new(
        Marshal.PtrToStringUni(info.Name) ?? throw new InvalidDataException("Driver has no name."),
        Marshal.PtrToStringUni(info.Environment) ?? "", info.Version, info.Date,
        Marshal.PtrToStringUni(info.InfPath) ?? "");

    [StructLayout(LayoutKind.Sequential)]
    private struct DriverInfo8
    {
        public uint Model;
        public IntPtr Name, Environment, DriverPath, DataFile, ConfigFile, HelpFile, DependentFiles,
            MonitorName, DefaultDataType, PreviousNames;
        public long Date;
        public ulong Version;
        public IntPtr Manufacturer, OemUrl, HardwareId, Provider, PrintProcessor, VendorSetup, ColorProfiles, InfPath;
        public uint Attributes;
        public IntPtr CoreDependencies;
        public long MinInboxDate;
        public ulong MinInboxVersion;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterDriverW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinterDriver(IntPtr printer, string environment, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDriversW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinterDrivers(string? server, string environment, uint level, IntPtr buffer,
        uint size, out uint needed, out uint count);
    [DllImport("setupapi.dll", EntryPoint = "SetupGetInfPublishedNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupGetInfPublishedName(string storePath, StringBuilder publishedPath, uint size, out uint needed);
    [DllImport("spoolss.dll", EntryPoint = "UploadPrinterDriverPackageW", CharSet = CharSet.Unicode)]
    private static extern int UploadPrinterDriverPackage(string? server, string inf, string environment,
        uint flags, IntPtr window, StringBuilder destination, ref uint size);
    [DllImport("spoolss.dll", EntryPoint = "InstallPrinterDriverFromPackageW", CharSet = CharSet.Unicode)]
    private static extern int InstallPrinterDriverFromPackage(string? server, string inf, string name,
        string environment, uint flags);
}
