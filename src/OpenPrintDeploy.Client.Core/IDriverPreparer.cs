using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

public interface IDriverPreparer
{
    Task<IReadOnlyList<PrinterApplyError>> PrepareAsync(
        IReadOnlyList<PrinterDto> printers, CancellationToken ct);
}
