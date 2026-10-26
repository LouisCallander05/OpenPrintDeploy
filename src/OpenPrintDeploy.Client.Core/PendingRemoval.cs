using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// The removal manifest written at uninstall by the SYSTEM-context gather step
/// and consumed, per user, at each profile's next logon.
///
/// <para>
/// Why a manifest at all: managed state and printer connections are both
/// per-user, but an MSI uninstall runs as SYSTEM and cannot delete another
/// (logged-off) user's printer connections — their HKU hive isn't loaded. So
/// SYSTEM records profile SIDs from HKLM without opening user-controlled files.
/// A per-user logon task reads that user's managed state and removes eligible
/// connections in the user's own context.
/// </para>
///
/// <para>
/// The shared request is immutable to standard users. Completion is stored in
/// each user's profile. Only Created-origin printers are eligible for removal.
/// </para>
/// </summary>
/// <param name="Sid">The user's security identifier (e.g. <c>S-1-5-21-…</c>).</param>
/// <param name="UserName">Best-effort display label for logs; not used for matching.</param>
/// <param name="Uncs">Legacy v1 field; v2 writes an empty list and reads per-user state instead.</param>
public sealed record PendingRemovalUser(string Sid, string? UserName, IReadOnlyList<string> Uncs);

/// <summary>The whole manifest. Versioned so a future format change is detectable.</summary>
/// <param name="Version">Schema version (currently 2).</param>
/// <param name="CreatedUtc">When the manifest was written, ISO-8601, for diagnostics.</param>
/// <param name="Users">One entry per eligible profile registered in HKLM.</param>
public sealed record PendingRemoval(
    int Version,
    string CreatedUtc,
    IReadOnlyList<PendingRemovalUser> Users)
{
    // v2 lists eligible profile SIDs. Each user reads their own managed state;
    // SYSTEM no longer reads user-controlled files or copies them into a manifest.
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a manifest, returning null on empty/corrupt input (uninstall paths must not throw).</summary>
    public static PendingRemoval? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingRemoval>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The list for a given SID, or null if this user has nothing to remove.</summary>
    public PendingRemovalUser? ForSid(string sid)
        => Users.FirstOrDefault(u => string.Equals(u.Sid, sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Legacy transformation helper. v2 removers never write back to the shared file.
    /// </summary>
    public PendingRemoval WithoutSid(string sid)
        => this with
        {
            Users = Users
                .Where(u => !string.Equals(u.Sid, sid, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
}

/// <summary>
/// Pure helpers that decide what is eligible for uninstall removal. Kept free of
/// any Windows API so the rules are unit-testable: only printers OPD itself
/// <see cref="PrinterOrigin.Created"/> are removed; adopted printers (left by a
/// prior tool, merely claimed by OPD) are handed back untouched, and a user's
/// own printers were never in the managed state to begin with.
/// </summary>
public static class PendingRemovalPlanner
{
    /// <summary>
    /// The UNCs from one user's managed set that uninstall may remove —
    /// Created-origin only, de-duplicated case-insensitively (matching Windows
    /// connection identity). Returns an empty list when nothing is eligible.
    /// </summary>
    public static IReadOnlyList<string> EligibleForRemoval(IEnumerable<ManagedPrinter> managed)
    {
        ArgumentNullException.ThrowIfNull(managed);

        return managed
            .Where(m => m.Origin == PrinterOrigin.Created)
            .Select(m => m.Unc)
            .Where(unc => !string.IsNullOrWhiteSpace(unc))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
