using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// Development-only authentication: takes the username from an
/// <c>X-Dev-User</c> request header, falling back to a configured default.
/// Stands in for Negotiate on dev machines that have no Kerberos. Never
/// registered when <c>Auth:Mode</c> is <c>Negotiate</c>.
/// </summary>
public sealed class DevAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Dev";
    private const string UserHeader = "X-Dev-User";

    private readonly string? _defaultUser;

    public DevAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        _defaultUser = authOptions.Value.Dev.DefaultUser;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var username = Request.Headers[UserHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(username))
        {
            username = _defaultUser;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            // Anonymous — let authorization decide (e.g. 401 on /sync).
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
