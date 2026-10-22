namespace OpenPrintDeploy.Shared.Drivers;

public static class DriverInstallErrors
{
    public static string Describe(int code) => unchecked((uint)code) switch
    {
        0x800F0242 => "The driver signing publisher is not trusted on this computer. "
            + "An administrator must verify the package's signing certificate and deploy it to the computer's "
            + "Trusted Publishers store, or supply an appropriately trusted driver package.",
        _ => "Windows could not prepare the driver package."
    };
}
