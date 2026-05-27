namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Canonicalises the name surfaced by the auth layer into a bare
/// sAMAccountName for directory lookups. Negotiate yields <c>DOMAIN\user</c>;
/// some flows yield a <c>user@domain</c> UPN. Both reduce to <c>user</c>.
/// </summary>
public static class DirectoryUsername
{
    public static string Normalize(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return string.Empty;
        }

        var name = username.Trim();

        var slash = name.LastIndexOf('\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var at = name.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            name = name[..at];
        }

        return name;
    }
}
