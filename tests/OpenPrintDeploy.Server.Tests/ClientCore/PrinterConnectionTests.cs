using System.ComponentModel;
using System.Runtime.Versioning;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

[SupportedOSPlatform("windows")]
public sealed class PrinterConnectionTests
{
    private static readonly PreparedDriver Prepared = new("Test driver", "Windows x64", 42, 123);
    private static readonly InstalledDriver Installed = new("Test driver", "Windows x64", 42, 123, "test.inf");

    [Fact]
    public void PreparedDriverIsVerifiedBeforeNormalSharedQueueConnection()
    {
        var verified = false;
        var calls = 0;
        WindowsPrinterConnection.AddCore(Prepared, name =>
        {
            Assert.Equal(Prepared.Name, name);
            verified = true;
            return Installed;
        }, (flags, name) =>
        {
            Assert.True(verified);
            calls++;
            // NO_UI only: never opt into mismatched local-driver rendering.
            Assert.Equal(0x40u, flags);
            Assert.Null(name);
            return 0;
        });
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1797)]
    [InlineData(3012)]
    public void ConnectionFailureReportsNativeErrorWithoutMismatchedOrInteractiveRetry(int error)
    {
        var calls = 0;
        var ex = Assert.Throws<Win32Exception>(() => WindowsPrinterConnection.AddCore(
            Prepared, _ => Installed, (flags, name) =>
            {
                calls++;
                Assert.Equal(0x40u, flags);
                Assert.Null(name);
                return error;
            }));
        Assert.Equal(error, ex.NativeErrorCode);
        Assert.Equal(1, calls);
        Assert.Contains(Prepared.Name, ex.Message);
        Assert.Contains("Interactive driver installation is disabled", ex.Message);
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

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void WithoutPreparedDriverOnlyStandardSilentSelectionIsAttempted(int error)
    {
        var calls = 0;
        void Connect() => WindowsPrinterConnection.AddCore(null,
            _ => throw new InvalidOperationException("Must not look up a driver"), (flags, name) =>
            {
                calls++;
                Assert.Equal(0x40u, flags);
                Assert.Null(name);
                return error;
            });
        if (error == 0) Connect();
        else Assert.Equal(error, Assert.Throws<Win32Exception>(Connect).NativeErrorCode);
        Assert.Equal(1, calls);
    }
}
