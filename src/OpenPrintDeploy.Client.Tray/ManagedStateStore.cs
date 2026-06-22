using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Persists the printers this client manages — each with its provenance
/// (<see cref="PrinterOrigin"/>) — under <c>%LOCALAPPDATA%\OpenPrintDeploy</c>.
/// The reconciler removes only printers recorded here, so the user's own
/// printers are never disturbed.
/// </summary>
public sealed class ManagedStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Store the origin as "Created"/"Adopted" rather than 0/1, so the file
        // stays legible and survives enum reordering.
        Converters = { new JsonStringEnumConverter() },
    };

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
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        // Current format: an array of { Unc, Origin } objects.
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ManagedPrinter>>(json, JsonOptions);
            if (parsed is not null && parsed.All(m => !string.IsNullOrWhiteSpace(m.Unc)))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Not the current format — fall through to the legacy reader.
        }

        // Legacy format (pre-provenance): a bare array of UNC strings, every one
        // of which was a printer OPD created. Migrate them as Created so they
        // remain removable on uninstall.
        try
        {
            var legacy = JsonSerializer.Deserialize<List<string>>(json);
            if (legacy is not null)
            {
                return legacy
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => new ManagedPrinter(u, PrinterOrigin.Created))
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Corrupt/unreadable — treat as no managed printers rather than throw.
        }

        return [];
    }

    public void Save(IReadOnlyList<ManagedPrinter> managed)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(managed, JsonOptions));
    }
}
