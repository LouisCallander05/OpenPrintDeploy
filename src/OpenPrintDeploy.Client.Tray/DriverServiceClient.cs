using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Tray;

public sealed class DriverServiceClient : IDriverPreparer
{
    public async Task<IReadOnlyList<PrinterApplyError>> PrepareAsync(IReadOnlyList<PrinterDto> printers, CancellationToken ct)
    {
        var failures = new List<PrinterApplyError>();
        foreach (var printer in printers.DistinctBy(p => p.UncPath, StringComparer.OrdinalIgnoreCase))
        {
            // Older servers continue their existing Point and Print behavior.
            if (string.IsNullOrEmpty(printer.DriverTicket)) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                using var pipe = new NamedPipeClientStream(".", DriverProtocol.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
                await pipe.ConnectAsync(3000, timeout.Token);
                await DriverWire.WriteAsync(pipe, new DriverRequest(printer.UncPath, printer.DriverTicket), timeout.Token);
                var result = await DriverWire.ReadAsync<DriverResponse>(pipe, timeout.Token);
                if (!result.Ready) failures.Add(new(printer, result.Error ?? "Driver service could not prepare the driver."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
            {
                failures.Add(new(printer, $"Driver service unavailable or timed out: {ex.Message}"));
            }
        }
        foreach (var failure in failures) ClientLog.Error($"Driver preparation for {failure.Printer.UncPath}: {failure.Message}");
        return failures;
    }
}
