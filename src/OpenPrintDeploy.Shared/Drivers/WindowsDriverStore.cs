using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenPrintDeploy.Shared.Drivers;

public sealed record InstalledDriver(string Name, string Environment, ulong Version, long Date, string InfPath)
{
    public IReadOnlyList<Guid> CoreDependencies { get; init; } = [];
}

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
            return ForPrinterHandle(handle);
        }
        finally { ClosePrinter(handle); }
    }

    internal static InstalledDriver ForPrinterHandle(IntPtr handle)
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

    public static InstalledDriver? Find(string name)
        => Find(name, null);

    internal static InstalledDriver? Find(string name, string? server)
    {
        EnumPrinterDrivers(server, DriverEnvironment, 8, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
            return null;
        }
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinterDrivers(server, DriverEnvironment, 8, buffer, needed, out _, out var count))
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
        => InstallStaged(Stage(infPath), name);

    // UploadPrinterDriverPackage handles ONE signed package, not its separate
    // core dependencies. Stage every core package before installing the vendor.
    public static string Stage(string infPath)
    {
        var destination = new StringBuilder(32768);
        var size = (uint)destination.Capacity;
        // UPDP_SILENT_UPLOAD. Windows rejects unsigned/altered packages here.
        Marshal.ThrowExceptionForHR(UploadPrinterDriverPackage(null, infPath, DriverEnvironment, 1,
            IntPtr.Zero, destination, ref size));
        return destination.ToString();
    }

    private static void InstallStaged(string infPath, string name)
    {
        Marshal.ThrowExceptionForHR(InstallPrinterDriverFromPackage(null, infPath, name,
            DriverEnvironment, 1)); // IPDFP_COPY_ALL_FILES: also refresh an existing driver.
    }

    public static IReadOnlyList<CoreDriverPackage> CoreDrivers(string? server, IReadOnlyList<Guid> dependencies)
    {
        if (dependencies.Count == 0) return [];
        if (dependencies.Count > CoreDriverRequirements.MaxDependencies || dependencies.Any(d => d == Guid.Empty)
            || dependencies.Distinct().Count() != dependencies.Count)
            throw new InvalidDataException("Invalid core driver dependency identifiers.");
        var multiString = string.Join('\0', dependencies.Select(d => d.ToString("B"))) + "\0\0";
        var pointer = Marshal.StringToHGlobalUni(multiString);
        try
        {
            var drivers = new CorePrinterDriver[dependencies.Count];
            Marshal.ThrowExceptionForHR(GetCorePrinterDrivers(server, DriverEnvironment, pointer, (uint)drivers.Length, drivers));
            var result = drivers.Select(d => new CoreDriverPackage(d.Id, d.Version, d.Date, d.Package)
            { PackageId = Path.GetFileName(Path.GetDirectoryName(d.Package)) }).ToList();
            CoreDriverRequirements.Verify(dependencies, result.Select(d => d.Identity).ToList(), result.Select(d => d.Identity).ToList());
            return result;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public static void VerifyCorePackages(InstalledDriver driver, IReadOnlyList<CoreDriverPackage>? expected)
    {
        if (driver.CoreDependencies.Count == 0) return;
        var identities = expected?.Select(d => d.Identity).ToList();
        // Validate completeness before making any native calls with metadata.
        CoreDriverRequirements.Verify(driver.CoreDependencies, identities, identities ?? []);
        CoreDriverRequirements.Verify(driver.CoreDependencies, identities,
            expected!.Where(IsCorePackageAvailable).Select(d => d.Identity).ToList());
    }

    public static bool IsCorePackageAvailable(CoreDriverPackage expected)
    {
        // GetCorePrinterDrivers reports the registered default. It may remain
        // older even after Windows has successfully staged the server's package.
        // Check the exact Windows-published package as well as an active newer
        // core. A mere file in a download cache is never sufficient.
        if (!string.IsNullOrWhiteSpace(expected.PackageId)
            && expected.PackageId == Path.GetFileName(expected.PackageId))
        {
            var repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "DriverStore", "FileRepository");
            var inf = DriverPackageFiles.Resolve(repository, expected.PackageId + "/" + Path.GetFileName(expected.InfPath));
            if (File.Exists(inf))
            {
                try
                {
                    var published = PublishedInf(inf);
                    // SetupGetInfPublishedName returns e.g. "oem53.inf",
                    // rather than an absolute path on current Windows builds.
                    var publishedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "INF", Path.GetFileName(published));
                    if (File.Exists(publishedPath)) return true;
                }
                catch (Win32Exception) { /* Not a Windows-published package. */ }
            }
        }
        Marshal.ThrowExceptionForHR(CorePrinterDriverInstalled(null, DriverEnvironment, expected.Id,
            expected.Date, expected.Version, out var installed));
        return installed;
    }

    public static void VerifyConnectionCorePackages(string unc, InstalledDriver serverDriver)
    {
        var host = unc.Split('\\', StringSplitOptions.RemoveEmptyEntries).First();
        VerifyCorePackages(serverDriver, CoreDrivers(@"\\" + host, serverDriver.CoreDependencies));
    }

    private static InstalledDriver Read(DriverInfo8 info) => new(
        Marshal.PtrToStringUni(info.Name) ?? throw new InvalidDataException("Driver has no name."),
        Marshal.PtrToStringUni(info.Environment) ?? "", info.Version, info.Date,
        Marshal.PtrToStringUni(info.InfPath) ?? "")
    { CoreDependencies = ReadDependencies(info.CoreDependencies) };

    private static IReadOnlyList<Guid> ReadDependencies(IntPtr pointer)
    {
        var result = new List<Guid>();
        if (pointer == IntPtr.Zero) return result;
        // A core dependency is a GUID, not a file path or arbitrary string.
        for (var offset = 0; offset <= CoreDriverRequirements.MaxDependencies * 39;)
        {
            if (Marshal.ReadInt16(pointer, offset * 2) == 0) return result;
            var value = Marshal.PtrToStringUni(pointer + offset * 2, 38);
            if (!Guid.TryParseExact(value, "B", out var id) || Marshal.ReadInt16(pointer, (offset + 38) * 2) != 0
                || result.Count == CoreDriverRequirements.MaxDependencies || result.Contains(id))
                throw new InvalidDataException("Invalid installed core driver dependency list.");
            result.Add(id);
            offset += 39;
        }
        throw new InvalidDataException("Unterminated core driver dependency list.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CorePrinterDriver
    {
        public Guid Id;
        public long Date;
        public ulong Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Package;
    }

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
    // These public client entry points are exported by winspool.drv. Binding to
    // spoolss.dll instead throws EntryPointNotFoundException before installation.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "UploadPrinterDriverPackageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int UploadPrinterDriverPackage(string? server, string inf, string environment,
        uint flags, IntPtr window, StringBuilder destination, ref uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "InstallPrinterDriverFromPackageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int InstallPrinterDriverFromPackage(string? server, string inf, string name,
        string environment, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "GetCorePrinterDriversW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCorePrinterDrivers(string? server, string environment, IntPtr dependencies,
        uint count, [Out] CorePrinterDriver[] drivers);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winspool.drv", EntryPoint = "CorePrinterDriverInstalledW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CorePrinterDriverInstalled(string? server, string environment, Guid id, long date,
        ulong version, [MarshalAs(UnmanagedType.Bool)] out bool installed);
}
