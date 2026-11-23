using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

public interface IPrinterConnectionRepairer
{
    // Returns only after completing a migration (or its interrupted recovery),
    // including preferences and default. Throwing leaves the revision pending.
    Task CheckAndRepairAsync(PrinterDto printer, PreparedDriver driver, CancellationToken ct,
        bool connectionJustAdded = false, Guid? requestId = null);
}

public static class PrinterConnectionMigration
{
    public const int CurrentRevision = 1;
}
