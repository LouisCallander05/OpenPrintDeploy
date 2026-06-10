namespace OpenPrintDeploy.Server.Updates;

/// <summary>
/// Configures the "Check for updates" feature in the admin UI. Bound from the
/// <c>Updates</c> configuration section.
/// </summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>
    /// The <c>owner/repo</c> on GitHub whose latest release the server compares
    /// against its own version. Empty disables the check (the button reports that
    /// no repo is configured).
    /// </summary>
    public string GitHubRepo { get; set; } = "BATSO123/OpenPrintDeploy";
}
