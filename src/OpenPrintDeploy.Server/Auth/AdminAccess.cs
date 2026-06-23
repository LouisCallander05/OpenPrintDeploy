namespace OpenPrintDeploy.Server.Auth;

/// <summary>The admin grants editable from the Settings page (persisted to disk).</summary>
public sealed record AdminAccess(IReadOnlyList<string> Groups, IReadOnlyList<string> Users)
{
    public static AdminAccess Empty { get; } = new([], []);
}

/// <summary>
/// Result of reading the stored admin grants. <see cref="Unreadable"/> is true
/// when the file is present but corrupt/tampered — distinct from a missing file,
/// so the system never re-opens on a damaged store.
/// </summary>
public sealed record AdminAccessLoad(AdminAccess Access, bool Unreadable);

/// <summary>
/// The effective admin set actually evaluated: the configured (appsettings)
/// grants unioned with the stored (Settings-page) grants.
/// </summary>
public sealed record EffectiveAdminAccess(
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> GroupSids,
    bool Sealed = false)
{
    /// <summary>
    /// Nothing configured anywhere → first-run "any authenticated user" mode. A
    /// sealed store (present-but-unreadable admin file) is never open: it falls
    /// back to the appsettings break-glass grants only, fail-closed.
    /// </summary>
    public bool IsOpen => !Sealed && Groups.Count == 0 && Users.Count == 0 && GroupSids.Count == 0;
}

/// <summary>The auth scheme names chosen for the current environment.</summary>
public sealed record AuthSchemes(string Admin, string Client);
