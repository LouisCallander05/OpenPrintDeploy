using OpenPrintDeploy.Shared.Sync;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Client.Core;

public interface IDriverPreparer
{
    Task<DriverPreparationOutcome> PrepareAsync(
        IReadOnlyList<PrinterDto> printers, CancellationToken ct);
}

public sealed record DriverPreparationOutcome(
    IReadOnlyList<PrinterApplyError> Failures,
    IReadOnlyDictionary<string, PreparedDriver> Drivers);
