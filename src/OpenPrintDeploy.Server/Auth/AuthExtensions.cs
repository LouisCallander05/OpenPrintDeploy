using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace OpenPrintDeploy.Server.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Registers authentication + the Admin authorization policy. The scheme
    /// is chosen by <see cref="IHostEnvironment.IsDevelopment"/> — Negotiate in
    /// production (the real AD path), the header-based Dev handler in
    /// development. We deliberately don't drive this off an <c>Auth:Mode</c>
    /// config knob: that read would happen before WebApplicationFactory's
    /// in-memory overrides apply, and registering Negotiate alongside it
    /// crashes the TestServer (no <c>IConnectionItemsFeature</c>).
    /// </summary>
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        if (env.IsDevelopment())
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
