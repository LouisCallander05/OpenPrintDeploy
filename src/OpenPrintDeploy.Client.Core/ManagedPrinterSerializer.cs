using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// One place that reads and writes the per-user <c>managed-printers.json</c>
/// state. Shared by the tray (which owns its own user's file) and the uninstall
/// cleanup tool (which reads <em>every</em> profile's file from the SYSTEM
/// context). Keeping the format in a single helper means the two can never drift
/// — a printer the tray recorded is parsed identically by the remover.
/// </summary>
public static class ManagedPrinterSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Store the origin as "Created"/"Adopted" rather than 0/1, so the file
        // stays legible and survives enum reordering.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parses managed-printers.json content into the set of printers OPD manages.
    /// Tolerant by design: unknown/corrupt content yields an empty set rather
    /// than throwing, because this runs in an uninstall path that must never fail.
    /// Handles both the current { Unc, Origin } array and the legacy bare-string
    /// array (migrated as <see cref="PrinterOrigin.Created"/>).
    /// </summary>
    public static IReadOnlyList<ManagedPrinter> Parse(string? json)
    {
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

    public static string Serialize(IReadOnlyList<ManagedPrinter> managed)
        => JsonSerializer.Serialize(managed, JsonOptions);
}
