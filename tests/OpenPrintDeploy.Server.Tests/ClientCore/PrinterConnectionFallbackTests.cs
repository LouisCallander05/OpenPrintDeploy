using System.ComponentModel;
using System.Runtime.Versioning;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

[SupportedOSPlatform("windows")]
public sealed class PrinterConnectionFallbackTests
{
    private static readonly PreparedDriver Prepared = new("Test driver", "Windows x64", 42, 123);
    private static readonly InstalledDriver Installed = new("Test driver", "Windows x64", 42, 123, "test.inf");

    [Fact]
    public void ExplicitSelectionSuccessDoesNotRetry()
    {
        var calls = 0;
        Assert.False(WindowsPrinterConnection.AddCore(Prepared, _ => Installed, (flags, name) =>
        {
            calls++;
            Assert.Equal(0x60u, flags);
            Assert.Equal(Prepared.Name, name);
            return 0;
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NotFoundRetriesOnceWithNormalSelectionAndNoUi()
    {
        var calls = new List<(uint Flags, string? Name)>();
        Assert.True(WindowsPrinterConnection.AddCore(Prepared, _ => Installed, (flags, name) =>
        {
            calls.Add((flags, name));
            return calls.Count == 1 ? 3012 : 0;
        }));
        Assert.Equal(new (uint, string?)[] { (0x60, Prepared.Name), (0x40, null) }, calls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1797)]
    public void OtherErrorsAreNotRetried(int error)
    {
        var calls = 0;
        var ex = Assert.Throws<Win32Exception>(() => WindowsPrinterConnection.AddCore(
            Prepared, _ => Installed, (_, _) => { calls++; return error; }));
        Assert.Equal(error, ex.NativeErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailedFallbackReportsBothAttemptsAndFinalWindowsError()
    {
        var calls = 0;
        var ex = Assert.Throws<Win32Exception>(() => WindowsPrinterConnection.AddCore(
            Prepared, _ => Installed, (flags, _) =>
            {
                Assert.NotEqual(0u, flags & 0x40);
                return ++calls == 1 ? 3012 : 5;
            }));
        Assert.Equal(2, calls);
        Assert.Equal(5, ex.NativeErrorCode);
        Assert.Contains("Explicit driver selection returned Win32 3012", ex.Message);
        Assert.Contains("standard silent selection also failed", ex.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("environment")]
    [InlineData("version")]
    [InlineData("date")]
    public void MissingOrChangedPreparedDriverBlocksAllConnectionAttempts(string change)
    {
        var installed = change switch
        {
            "missing" => null,
            "environment" => Installed with { Environment = "Windows ARM64" },
            "version" => Installed with { Version = 43 },
            _ => Installed with { Date = 124 }
        };
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => WindowsPrinterConnection.AddCore(
            Prepared, _ => installed, (_, _) => { calls++; return 0; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void WithoutPreparedDriverOnlyStandardSilentSelectionIsAttempted()
    {
        var calls = 0;
        var ex = Assert.Throws<Win32Exception>(() => WindowsPrinterConnection.AddCore(null,
            _ => throw new InvalidOperationException("Must not look up a driver"), (flags, name) =>
            {
                calls++;
                Assert.Equal(0x40u, flags);
                Assert.Null(name);
                return 3012;
            }));
        Assert.Equal(3012, ex.NativeErrorCode);
        Assert.Equal(1, calls);
    }
}
