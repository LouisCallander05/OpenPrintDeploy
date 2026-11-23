using System.IO;
using Microsoft.Win32;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Tray;

public sealed class WindowsPrinterConnectionRepairer : IPrinterConnectionRepairer
{
    private readonly IReadOnlyCollection<string> _allowedPrintServers;
    private readonly FilePrinterRepairBackupStore _backups;
    private readonly PrinterConnectionRepairer _repairer;

    public WindowsPrinterConnectionRepairer(IReadOnlyCollection<string> allowedPrintServers)
    {
        _allowedPrintServers = allowedPrintServers;
        _backups = new FilePrinterRepairBackupStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenPrintDeploy", "connection-repairs", $"v{PrinterConnectionMigration.CurrentRevision}"));
        _repairer = new PrinterConnectionRepairer(new WindowsPrinterRepairOperations(), _backups, ClientLog.Info);
    }

    public async Task CheckAndRepairAsync(PrinterDto printer, PreparedDriver driver, CancellationToken ct,
        bool connectionJustAdded = false, Guid? requestId = null)
    {
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OpenPrintDeploy\Client");
        var enabled = Convert.ToString(policy?.GetValue("ConnectionRepairsEnabled"));
        if (enabled is "0" || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connection repairs are paused by machine policy.");
        if (!PrinterUncPolicy.IsAllowed(printer.UncPath, _allowedPrintServers, out var reason))
            throw new InvalidOperationException($"Connection repair blocked: {reason}");

        using var repairLock = _backups.Acquire(printer.UncPath);
        await _repairer.CheckAndRepairAsync(printer, driver, ct, connectionJustAdded, requestId);
    }

    public void CompleteSavedMigrations(IReadOnlyCollection<string> completed)
    {
        foreach (var unc in completed)
        {
            try
            {
                using var repairLock = _backups.Acquire(unc);
                _backups.Complete(unc);
            }
            catch (IOException ex)
            {
                ClientLog.Warn($"Could not archive completed repair backup for {unc}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                ClientLog.Warn($"Could not archive completed repair backup for {unc}: {ex.Message}");
            }
        }
    }
}
