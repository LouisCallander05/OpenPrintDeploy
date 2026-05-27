using System.IO;
using System.Text.Json;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Persists the set of printer UNCs this client deployed, under
/// <c>%LOCALAPPDATA%\OpenPrintDeploy</c>. The reconciler removes only printers
/// recorded here, so the user's own printers are never disturbed.
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

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<string> managed)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(managed));
    }
}
