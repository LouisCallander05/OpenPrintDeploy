namespace OpenPrintDeploy.Server.Admin;

/// <summary>
/// Thrown when an admin operation fails a business rule (e.g. a duplicate
/// name or UNC path). Carries a user-facing message that pages surface
/// directly in an alert.
/// </summary>
public sealed class AdminValidationException : Exception
{
    public AdminValidationException(string message) : base(message)
    {
    }
}
