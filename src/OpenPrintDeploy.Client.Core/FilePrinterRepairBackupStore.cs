using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Client.Core;

public sealed class FilePrinterRepairBackupStore(string directory) : IPrinterRepairBackupStore
{
    private string PathFor(string unc)
        => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(unc.ToUpperInvariant()))) + ".json");

    public IDisposable Acquire(string unc)
    {
        Directory.CreateDirectory(directory);
        // Per-profile file sharing also serializes repair across user sessions.
        return new FileStream(PathFor(unc) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PrinterRepairBackup? Load(string unc)
    {
        var path = PathFor(unc);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 262144) throw new InvalidDataException("Repair backup is too large.");
        var backup = JsonSerializer.Deserialize<PrinterRepairBackup>(File.ReadAllText(path));
        if (backup is null || string.IsNullOrWhiteSpace(backup.Unc) || string.IsNullOrWhiteSpace(backup.DriverName)
            || (backup.UserDevMode is not null && backup.UserDevMode.Length < 220))
            throw new InvalidDataException("Invalid connection repair backup; leaving the queue unchanged.");
        return backup;
    }

    public void Save(PrinterRepairBackup backup)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(backup.Unc);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, backup);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public void Complete(string unc)
    {
        var path = PathFor(unc);
        if (File.Exists(path))
            File.Move(path, path + ".completed-" + Guid.NewGuid().ToString("N"), overwrite: false);
    }
}
