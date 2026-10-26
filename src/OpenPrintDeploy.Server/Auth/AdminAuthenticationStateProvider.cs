using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace OpenPrintDeploy.Server.Auth;

// Also invalidate idle pages, including pages with server-driven refresh timers.
// The circuit activity guard separately blocks the next client action immediately.
public sealed class AdminAuthenticationStateProvider(ILoggerFactory loggerFactory, AdminSession sessions,
    AdminAccessEvaluator access, AuthSchemes schemes) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(15);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
        => (schemes.Admin != AdminSession.Scheme || sessions.IsActive(state.User))
            && await access.IsAdminAsync(state.User, ct);
}
