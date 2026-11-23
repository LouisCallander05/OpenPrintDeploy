using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

public interface IPrinterRepairBackupStore
{
    PrinterRepairBackup? Load(string unc);
    // Must persist successfully before any disconnect. Keep the original
    // snapshot through retries and client restarts until state records success.
    void Save(PrinterRepairBackup backup);
}

public sealed class PrinterConnectionRepairer(
    IPrinterRepairOperations printers,
    IPrinterRepairBackupStore backups,
    Action<string>? log = null) : IPrinterConnectionRepairer
{
    public Task CheckAndRepairAsync(PrinterDto printer, PreparedDriver driver, CancellationToken ct,
        bool connectionJustAdded = false, Guid? requestId = null)
    {
        ct.ThrowIfCancellationRequested();
        var unc = printer.UncPath;
        printers.VerifyDriver(unc, driver);
        var backup = backups.Load(unc);
        if (backup is not null && (!backup.Unc.Equals(unc, StringComparison.OrdinalIgnoreCase)
            || !backup.DriverName.Equals(driver.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The pending repair backup belongs to a different connection or driver.");

        // Live server/driver verification is required even when its defaults
        // are simplex. The migration is based on the old connection revision,
        // not a guess from a missing feature or a printer/driver name.
        var expectsDuplex = printers.ServerDefaultsToDuplex(unc, driver);

        if (printers.HasPendingUserJobs(unc))
            throw new InvalidOperationException("Waiting for this user's queued print jobs to finish before connection repair.");

        if (backup is null)
        {
            backup = printers.Capture(unc, driver);
            backups.Save(backup);
        }
        if (requestId is not null && backup.RequestId != requestId)
        {
            // A new explicit admin request forces one reconnect even if the
            // previous command finished. Preserve any original recovery data.
            backup = backup with { RequestId = requestId, Reconnected = false };
            backups.Save(backup);
        }
        if (connectionJustAdded)
        {
            backup = backup with { Reconnected = true };
            backups.Save(backup);
        }

        ct.ThrowIfCancellationRequested();
        // Once disconnected, finish reconnect/recovery even during shutdown.
        // A durable backup lets the next sync recover after a process crash.
        try
        {
            if (!backup.Reconnected)
            {
                printers.Disconnect(unc);
                try
                {
                    printers.Connect(unc, driver);
                }
                catch
                {
                    // One bounded recovery attempt through the same silent
                    // standard connection path; never restore mismatched mode.
                    printers.Connect(unc, driver);
                }
                backup = backup with { Reconnected = true };
                backups.Save(backup);
            }
            printers.RestorePreferences(unc, backup);
            if (expectsDuplex && !printers.SupportsDuplex(unc))
                throw new InvalidOperationException("The reconnected queue still does not advertise duplex; repair remains pending.");
        }
        finally
        {
            printers.RestoreDefault(backup);
        }

        log?.Invoke($"Connection revision 1 migrated and verified: {unc}.");
        return Task.CompletedTask;
    }
}
