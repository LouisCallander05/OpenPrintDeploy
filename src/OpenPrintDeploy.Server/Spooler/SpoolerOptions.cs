namespace OpenPrintDeploy.Server.Spooler;

/// <summary>Bound from the <c>Spooler</c> configuration section.</summary>
public sealed class SpoolerOptions
{
    public const string SectionName = "Spooler";

    /// <summary>
    /// The hostname clients use to reach this print server. Defaults to
    /// <see cref="Environment.MachineName"/> when null/blank. Override for
    /// cluster names or DNS aliases (e.g. <c>printsrv01.corp.local</c>) so the
    /// UNCs the import builds are the ones clients actually resolve.
    /// </summary>
    public string? ServerName { get; set; }
}
