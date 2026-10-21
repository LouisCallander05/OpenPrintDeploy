using Microsoft.AspNetCore.DataProtection;

namespace OpenPrintDeploy.Server.Drivers;

public sealed class DriverTickets(IDataProtectionProvider provider)
{
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("OpenPrintDeploy.DriverDownload.v1").ToTimeLimitedDataProtector();

    public string Issue(string unc) => _protector.Protect(unc, TimeSpan.FromMinutes(30));
    public string Read(string ticket) => _protector.Unprotect(ticket, out _);
}
