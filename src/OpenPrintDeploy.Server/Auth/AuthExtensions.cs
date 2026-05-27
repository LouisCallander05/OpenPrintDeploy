using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace OpenPrintDeploy.Server.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Registers authentication + the Admin authorization policy. The scheme is
    /// chosen by <c>Auth:Mode</c>: <c>Negotiate</c> (production) or <c>Dev</c>
    /// (the header-based dev handler). Negotiate is never registered in Dev mode,
    /// so a dev box without Kerberos never constructs that handler.
    /// </summary>
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        var mode = configuration[$"{AuthOptions.SectionName}:Mode"] ?? "Negotiate";

        if (mode.Equals(DevAuthenticationHandler.SchemeName, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAuthentication(DevAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(
                    DevAuthenticationHandler.SchemeName, configureOptions: null);
        }
        else
        {
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                .AddNegotiate();
        }

        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, policy => policy.Requirements.Add(new AdminRequirement()));

        return services;
    }
}
