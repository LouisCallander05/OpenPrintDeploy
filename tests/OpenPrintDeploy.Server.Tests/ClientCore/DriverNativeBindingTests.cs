using System.Reflection;
using System.Runtime.InteropServices;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class DriverNativeBindingTests
{
    [Theory]
    [InlineData(typeof(WindowsDriverStore), "UploadPrinterDriverPackage")]
    [InlineData(typeof(WindowsDriverStore), "InstallPrinterDriverFromPackage")]
    [InlineData(typeof(WindowsDriverStore), "GetCorePrinterDrivers")]
    [InlineData(typeof(WindowsDriverStore), "CorePrinterDriverInstalled")]
    [InlineData(typeof(WindowsServerQueueDriver), "EnumPrinters")]
    [InlineData(typeof(WindowsPrinterConnection), "AddPrinterConnection2")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "OpenPrinter2")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "ClosePrinter")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "GetPrinter")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "DeviceCapabilities")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "EnumJobs")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "DeletePrinterConnection")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "DocumentProperties")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "SetPrinter")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "GetDefaultPrinter")]
    [InlineData(typeof(WindowsPrinterRepairOperations), "SetDefaultPrinter")]
    public void EntryPointExistsInDeclaredWindowsLibrary(Type declaringType, string methodName)
    {
        if (!OperatingSystem.IsWindows()) return;

        // Resolve the actual production declaration without invoking an installer
        // or changing any printer/driver state. Compilation alone misses bad DLLs.
        var method = declaringType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        var import = Assert.IsType<DllImportAttribute>(method!.GetCustomAttribute<DllImportAttribute>());
        var library = NativeLibrary.Load(import.Value, typeof(WindowsDriverStore).Assembly, DllImportSearchPath.System32);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(library, import.EntryPoint!, out _),
                $"{import.EntryPoint} is missing from {import.Value}.");
        }
        finally { NativeLibrary.Free(library); }
    }

    [Fact]
    public void ConnectionInfoMatchesWindowsAbi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = typeof(WindowsPrinterConnection).GetNestedType("ConnectionInfo", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Zero, Marshal.OffsetOf(info, "Flags"));
        Assert.Equal(new IntPtr(IntPtr.Size), Marshal.OffsetOf(info, "DriverName"));
        Assert.Equal(IntPtr.Size * 2, Marshal.SizeOf(info));
    }

    [Fact]
    public void CorePrinterDriverMatchesWindowsAbi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = typeof(WindowsDriverStore).GetNestedType("CorePrinterDriver", BindingFlags.NonPublic)!;
        Assert.Equal(new IntPtr(16), Marshal.OffsetOf(info, "Date"));
        Assert.Equal(new IntPtr(24), Marshal.OffsetOf(info, "Version"));
        Assert.Equal(new IntPtr(32), Marshal.OffsetOf(info, "Package"));
        Assert.Equal(552, Marshal.SizeOf(info));
    }

    [Fact]
    public void ServerPrinterInfoMatchesWindowsAbi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = typeof(WindowsServerQueueDriver).GetNestedType("PrinterInfo2", BindingFlags.NonPublic)!;
        Assert.Equal(new IntPtr(2 * IntPtr.Size), Marshal.OffsetOf(info, "Share"));
        Assert.Equal(new IntPtr(4 * IntPtr.Size), Marshal.OffsetOf(info, "Driver"));
        Assert.Equal(13 * IntPtr.Size + 32, Marshal.SizeOf(info));
    }
}
