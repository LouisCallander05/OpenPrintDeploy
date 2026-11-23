namespace OpenPrintDeploy.Shared.Drivers;

public sealed record PrinterRepairBackup(string Unc, string DriverName, string? DefaultPrinter, byte[]? UserDevMode)
{
    public bool WasDefault { get; init; }
    public bool Reconnected { get; init; }
    public Guid? RequestId { get; init; }
}

public interface IPrinterRepairOperations
{
    void VerifyDriver(string unc, PreparedDriver driver);
    bool SupportsDuplex(string unc);
    bool ServerDefaultsToDuplex(string unc, PreparedDriver driver);
    bool HasPendingUserJobs(string unc);
    PrinterRepairBackup Capture(string unc, PreparedDriver driver);
    void Disconnect(string unc);
    void Connect(string unc, PreparedDriver driver);
    void RestorePreferences(string unc, PrinterRepairBackup backup);
    void RestoreDefault(PrinterRepairBackup backup);
}
