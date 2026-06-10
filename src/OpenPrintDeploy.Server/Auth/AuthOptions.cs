namespace OpenPrintDeploy.Server.Auth;

/// <summary>Bound from the <c>Auth</c> configuration section.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><c>Negotiate</c> for Windows Integrated Auth, <c>Dev</c> for local development.</summary>
    public string Mode { get; set; } = "Negotiate";

    /// <summary>
    /// How the admin browser UI signs in (production only — dev always uses the
    /// Dev header scheme). <c>Basic</c> prompts for a domain username/password
    /// validated against AD; <c>Negotiate</c> is the legacy Windows-SSO behaviour.
    /// This is the escape hatch: set it to <c>Negotiate</c> and restart to revert
    /// without reinstalling.
    /// </summary>
    public string AdminScheme { get; set; } = "Basic";

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
    /// Break-glass admin grants from configuration, honoured IN ADDITION to the
    /// editable Settings-page list. Edit appsettings + restart to recover access
    /// if the Settings page ever locks you out. When Groups, Users, GroupSids AND
    /// the stored list are all empty, any authenticated user is an admin
    /// (first-run bootstrap).
    /// </summary>
    public List<string> Groups { get; } = [];

    /// <summary>Admin usernames (sAMAccountName, <c>DOMAIN\user</c>, or UPN).</summary>
    public List<string> Users { get; } = [];

    /// <summary>Legacy: admin group SIDs. Still honoured for backward compatibility.</summary>
    public List<string> GroupSids { get; } = [];
}
