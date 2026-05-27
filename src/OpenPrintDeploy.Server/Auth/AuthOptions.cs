namespace OpenPrintDeploy.Server.Auth;

/// <summary>Bound from the <c>Auth</c> configuration section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><c>Negotiate</c> for Windows Integrated Auth, <c>Dev</c> for local development.</summary>
    public string Mode { get; set; } = "Negotiate";

    public DevAuthOptions Dev { get; set; } = new();

    public AdminAuthOptions Admin { get; set; } = new();
}

public sealed class DevAuthOptions
{
    /// <summary>Username assumed when no <c>X-Dev-User</c> header is present.</summary>
    public string? DefaultUser { get; set; }
}

public sealed class AdminAuthOptions
{
    /// <summary>
    /// Group SIDs that grant admin-UI access. Empty means any authenticated
    /// user is treated as an admin (suitable for single-operator/dev setups).
    /// </summary>
    public List<string> GroupSids { get; } = [];
}
