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
    [InlineData(typeof(WindowsPrinterConnection), "AddPrinterConnection2")]
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
}
