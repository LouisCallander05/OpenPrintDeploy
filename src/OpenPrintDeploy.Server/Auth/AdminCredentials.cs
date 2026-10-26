using System.ComponentModel;
using System.Security.Claims;

namespace OpenPrintDeploy.Server.Auth;

public sealed record AdminCredentialResult(ClaimsPrincipal? Principal, int ErrorCode = 0)
{
    // Windows distinguishes bad credentials from unavailable domain services.
    // Only explicit credential failures count toward the password throttle.
    public bool InvalidCredentials => ErrorCode is 86 or 1317 or 1326;
}

public interface IAdminCredentials
{
    Task<AdminCredentialResult> ValidateAsync(string username, string password, CancellationToken ct);
}

/// <summary>Bounds native logon work; slow DC calls cannot consume unlimited workers.</summary>
public sealed class AdminCredentials(ILogger<AdminCredentials> logger) : IAdminCredentials
{
    private readonly SemaphoreSlim _slots = new(4);

    public async Task<AdminCredentialResult> ValidateAsync(string username, string password, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new(null, 50);
        if (!await _slots.WaitAsync(0, ct)) return new(null, 1723);

        // The native call cannot be cancelled. Its slot remains occupied until
        // it really exits, even if the HTTP request times out or is cancelled.
        var work = Task.Run(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return new AdminCredentialResult(null, 50);
                using var identity = WindowsLogon.Validate(username, password, out var error);
                if (identity is null)
                {
                    logger.LogWarning("Admin Windows sign-in failed for {User}: {ErrorCode} ({Reason}).",
                        username, error, new Win32Exception(error).Message);
                    return new AdminCredentialResult(null, error);
                }
                var claims = new ClaimsIdentity(identity.Claims.ToList(), AdminSession.Scheme,
                    identity.NameClaimType, identity.RoleClaimType);
                return new AdminCredentialResult(new ClaimsPrincipal(claims));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Admin Windows sign-in failed unexpectedly for {User}.", username);
                return new AdminCredentialResult(null, 1359);
            }
            finally { _slots.Release(); }
        });
        try { return await work.WaitAsync(TimeSpan.FromSeconds(20), ct); }
        catch (TimeoutException)
        {
            logger.LogWarning("Admin Windows sign-in timed out for {User}.", username);
            return new(null, 1460);
        }
    }
}
