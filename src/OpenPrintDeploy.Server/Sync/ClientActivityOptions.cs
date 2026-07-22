namespace OpenPrintDeploy.Server.Sync;

public sealed class ClientActivityOptions
{
    public const string SectionName = "ClientActivity";
    public int RetentionDays { get; set; } = 30;
    public int OnlineWindowMinutes { get; set; } = 15;
}
