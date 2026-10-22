using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class DriverProcessOutputTests
{
    [Fact]
    public async Task DiagnosticPreservesExceptionAndDrainsExcessOutputWithBoundedMemory()
    {
        const string failure = "System.EntryPointNotFoundException: Missing installer function.\r\n";
        var output = failure + new string('x', 100_000);
        using var reader = new StringReader(output);

        var result = await DriverProcessOutput.ReadDiagnosticAsync(reader, CancellationToken.None);

        Assert.StartsWith(failure, result);
        Assert.Equal(DriverProcessOutput.MaxDiagnosticCharacters, result.Length);
        Assert.Equal(-1, reader.Peek());
    }

    [Fact]
    public async Task DiagnosticHonoursInstallationDeadline()
    {
        using var reader = new StringReader("error");
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DriverProcessOutput.ReadDiagnosticAsync(reader, timeout.Token));
    }
}
