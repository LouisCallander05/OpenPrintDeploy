using System.Text.Json;

namespace OpenPrintDeploy.Client.Core;

public sealed class FileRemoteRepairStateStore(string directory) : IRemoteRepairStateStore
{
    private string PathFor(Guid requestId) => Path.Combine(directory, requestId.ToString("N") + ".json");
    public IReadOnlyCollection<string> Load(Guid requestId)
    {
        var path = PathFor(requestId);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 1048576) throw new InvalidDataException("Repair progress file is too large.");
        var completed = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid repair progress.");
        if (completed.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid completed queue in repair progress.");
        return completed;
    }
    public void Save(Guid requestId, IReadOnlyCollection<string> completed)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(requestId);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, completed);
            file.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
