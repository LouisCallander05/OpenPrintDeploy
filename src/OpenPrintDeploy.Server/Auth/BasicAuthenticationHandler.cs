using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Https;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// HTTP Basic authentication for the admin browser UI, validated against AD via
/// an LDAP bind (see <see cref="IDirectoryService.ValidateCredentialsAsync"/>).
/// When HTTPS is available it refuses to read credentials over plain HTTP and
/// redirects to HTTPS instead, so passwords are only ever entered on TLS. The
/// <c>/sync</c> API is unaffected — it stays on Negotiate.
/// </summary>
public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Basic";

    private readonly IDirectoryService _directory;
    private readonly HttpsStatus _https;

    public BasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDirectoryService directory,
        HttpsStatus https)
        : base(options, logger, encoder)
    {
        _directory = directory;
        _https = https;
    }

    /// <summary>True when we must refuse credentials on this (non-TLS) request.</summary>
    private bool RequireHttpsButMissing => !Request.IsHttps && _https.Bound;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Never read a password over plaintext HTTP when HTTPS is available.
        if (RequireHttpsButMissing)
        {
            return AuthenticateResult.NoResult();
        }

        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string username;
        string password;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var sep = decoded.IndexOf(':');
            if (sep < 0)
            {
                return AuthenticateResult.Fail("Malformed Basic credentials.");
            }

            username = decoded[..sep];
            password = decoded[(sep + 1)..];
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return AuthenticateResult.Fail("Missing username or password.");
        }

        var valid = await _directory.ValidateCredentialsAsync(username, password, Context.RequestAborted);
        if (!valid)
        {
            return AuthenticateResult.Fail("Invalid username or password.");
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username.Trim())], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
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
