namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ClientDeviceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? DeviceIdentifier { get; set; }
    public required string MachineName { get; set; }
    public required string NormalizedMachineName { get; set; }
    public string? ClientVersion { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<ClientUserEntity> Users { get; set; } = new List<ClientUserEntity>();
}
