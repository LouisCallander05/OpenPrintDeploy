using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Https;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// HTTP Basic authentication for the admin browser UI. Credentials are validated
/// with the Windows LogonUser API (<see cref="WindowsLogon"/>), which also yields
/// the user's full cross-domain group set (the PAC) — so group-based admin works
/// across domains just like Windows SSO would. When HTTPS is available it refuses
/// to read credentials over plain HTTP and redirects to HTTPS instead, so
/// passwords are only ever entered on TLS. The <c>/sync</c> API is unaffected —
/// it stays on Negotiate.
/// </summary>
public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Basic";

    private readonly HttpsStatus _https;
    private readonly LoginThrottle _throttle;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        HttpsStatus https,
        LoginThrottle throttle)
        : base(options, logger, encoder)
    {
        _https = https;
        _throttle = throttle;
    }

    /// <summary>True when we must refuse credentials on this (non-TLS) request.</summary>
    private bool RequireHttpsButMissing => !Request.IsHttps && _https.Bound;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Never read a password over plaintext HTTP when HTTPS is available.
        if (RequireHttpsButMissing)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string username;
        string password;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var sep = decoded.IndexOf(':');
            if (sep < 0)
            {
                return Task.FromResult(AuthenticateResult.Fail("Malformed Basic credentials."));
            }

            username = decoded[..sep];
            password = decoded[(sep + 1)..];
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed Basic credentials."));
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing username or password."));
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(AuthenticateResult.Fail("Basic authentication requires Windows."));
        }

        // Brute-force throttle: once a username has failed enough times, reject
        // without touching AD for the cooldown — both slowing guessing and
        // keeping repeated bad binds from locking out the real AD account.
        var throttleKey = DirectoryUsername.Normalize(username);
        if (_throttle.IsLockedOut(throttleKey, out var retryAfter))
        {
            Logger.LogWarning("Admin sign-in throttled for {User}: {Seconds}s remaining.",
                throttleKey, (int)retryAfter.TotalSeconds);
            return Task.FromResult(AuthenticateResult.Fail("Too many failed attempts. Try again shortly."));
        }

        ClaimsIdentity identity;
        try
        {
            using var windows = WindowsLogon.Validate(username.Trim(), password);
            if (windows is null)
            {
                _throttle.RecordFailure(throttleKey);
                return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
            }

            _throttle.RecordSuccess(throttleKey);

            // Copy the claims (Name + group SIDs) out of the Windows identity into
            // a plain one, so we don't retain a Windows token handle per request.
            identity = new ClaimsIdentity(
                windows.Claims.ToList(), SchemeName, windows.NameClaimType, windows.RoleClaimType);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "LogonUser failed unexpectedly for {User}.", username);
            return Task.FromResult(AuthenticateResult.Fail("Sign-in failed."));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // On plain HTTP, bounce to HTTPS so the password is only entered over TLS.
        if (!Request.IsHttps && _https is { Bound: true, Port: > 0 })
        {
            Response.Redirect(
                $"https://{Request.Host.Host}:{_https.Port}{Request.PathBase}{Request.Path}{Request.QueryString}");
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Basic realm=\"Open Print Deploy\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
