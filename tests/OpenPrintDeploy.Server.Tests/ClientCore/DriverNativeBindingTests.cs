using System.Reflection;
using System.Runtime.InteropServices;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class DriverNativeBindingTests
{
    [Theory]
    [InlineData("UploadPrinterDriverPackage")]
    [InlineData("InstallPrinterDriverFromPackage")]
    public void PackageInstallerEntryPointExistsInDeclaredWindowsLibrary(string methodName)
    {
        if (!OperatingSystem.IsWindows()) return;

        // Resolve the actual production declaration without invoking an installer
        // or changing any printer/driver state. Compilation alone misses bad DLLs.
        var method = typeof(WindowsDriverStore).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        var import = Assert.IsType<DllImportAttribute>(method!.GetCustomAttribute<DllImportAttribute>());
        var library = NativeLibrary.Load(import.Value, typeof(WindowsDriverStore).Assembly, DllImportSearchPath.System32);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(library, import.EntryPoint!, out _),
                $"{import.EntryPoint} is missing from {import.Value}.");
        }
        finally { NativeLibrary.Free(library); }
    }
}
