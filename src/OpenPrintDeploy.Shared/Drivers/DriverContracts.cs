namespace OpenPrintDeploy.Shared.Drivers;

public static class DriverProtocol
{
    public const string ServiceName = "OpenPrintDeployDrivers";
    public const string PipeName = "OpenPrintDeploy.Drivers.v1";
    public const int MaxMessageBytes = 32768;
    public const long MaxPackageBytes = 512L * 1024 * 1024;
}

public sealed record DriverRequest(string UncPath, string Ticket);
public sealed record DriverResponse(bool Ready, string? Error = null, PreparedDriver? Driver = null);
public sealed record PreparedDriver(string Name, string Environment, ulong Version, long Date);
public sealed record DriverManifest(string UncPath, string DriverName, string Environment,
    ulong Version, long Date, string InfPath, string Sha256, long Length);
