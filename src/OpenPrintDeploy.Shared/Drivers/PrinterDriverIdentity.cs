namespace OpenPrintDeploy.Shared.Drivers;

public static class PrinterDriverIdentity
{
    public static void Verify(InstalledDriver? actual, PreparedDriver expected, string scope)
    {
        if (actual is not null && actual.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase)
            && actual.Environment == expected.Environment && actual.Version == expected.Version && actual.Date == expected.Date)
            return;
        throw new InvalidOperationException($"The {scope} driver does not match the prepared driver. "
            + $"Expected '{expected.Name}', {expected.Environment}, version {expected.Version}, date {expected.Date}; "
            + (actual is null ? "found no driver." : $"found '{actual.Name}', {actual.Environment}, version {actual.Version}, date {actual.Date}.")
            + " Retry after driver preparation.");
    }
}
