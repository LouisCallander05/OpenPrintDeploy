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

        // Scheme choice is env-based (not a config read that would happen before
        // WebApplicationFactory's overrides apply). Dev: the header-based Dev
        // handler for both the admin UI and /sync. Production: Basic for the
        // admin browser UI (default scheme, so the UI authenticates + displays as
        // the typed admin account) and Negotiate for the /sync client API. An
        // operator can fall back to legacy Windows-SSO admin via Auth:AdminScheme.
        string adminScheme;
        string clientScheme;
        if (env.IsDevelopment())
        {
            adminScheme = clientScheme = DevAuthenticationHandler.SchemeName;
            services.AddAuthentication(DevAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(
                    DevAuthenticationHandler.SchemeName, configureOptions: null);
        }
        else
        {
            clientScheme = NegotiateDefaults.AuthenticationScheme;
            var useNegotiateAdmin = string.Equals(
                configuration["Auth:AdminScheme"], "Negotiate", StringComparison.OrdinalIgnoreCase);

            if (useNegotiateAdmin)
            {
                // Legacy: Windows SSO for the admin UI too.
                adminScheme = NegotiateDefaults.AuthenticationScheme;
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate();
            }
            else
            {
                adminScheme = BasicAuthenticationHandler.SchemeName;
                services.AddAuthentication(BasicAuthenticationHandler.SchemeName)
                    .AddNegotiate()
                    .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                        BasicAuthenticationHandler.SchemeName, configureOptions: null);
            }
        }

        services.AddSingleton(new AuthSchemes(adminScheme, clientScheme));
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<AdminAccessStore>();
        services.AddSingleton<AdminAccessEvaluator>();
        services.AddScoped<IAuthorizationHandler, AdminAuthorizationHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, policy =>
            {
                policy.AddAuthenticationSchemes(adminScheme);
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new AdminRequirement());
            });

        return services;
    }
}
