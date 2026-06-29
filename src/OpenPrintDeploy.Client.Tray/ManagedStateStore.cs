using System.IO;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Persists the printers this client manages — each with its provenance
/// (<see cref="PrinterOrigin"/>) — under <c>%LOCALAPPDATA%\OpenPrintDeploy</c>.
/// The reconciler removes only printers recorded here, so the user's own
/// printers are never disturbed. The actual JSON shape lives in
/// <see cref="ManagedPrinterSerializer"/> so the uninstall cleanup tool reads
/// every profile's file the exact same way.
/// </summary>
public sealed class ManagedStateStore
{
    private readonly string _path;

    public ManagedStateStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenPrintDeploy");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "managed-printers.json");
    }

    public IReadOnlyList<ManagedPrinter> Load()
        => File.Exists(_path)
            ? ManagedPrinterSerializer.Parse(File.ReadAllText(_path))
            : [];

    public void Save(IReadOnlyList<ManagedPrinter> managed)
        => File.WriteAllText(_path, ManagedPrinterSerializer.Serialize(managed));
}
