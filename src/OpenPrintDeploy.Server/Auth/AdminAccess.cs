namespace OpenPrintDeploy.Server.Auth;

/// <summary>The admin grants editable from the Settings page (persisted to disk).</summary>
public sealed record AdminAccess(IReadOnlyList<string> Groups, IReadOnlyList<string> Users)
{
    public static AdminAccess Empty { get; } = new([], []);
}

/// <summary>
/// The effective admin set actually evaluated: the configured (appsettings)
/// grants unioned with the stored (Settings-page) grants.
/// </summary>
public sealed record EffectiveAdminAccess(
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> GroupSids)
{
    /// <summary>Nothing configured anywhere → first-run "any authenticated user" mode.</summary>
    public bool IsOpen => Groups.Count == 0 && Users.Count == 0 && GroupSids.Count == 0;
}

/// <summary>The auth scheme names chosen for the current environment.</summary>
public sealed record AuthSchemes(string Admin, string Client);
