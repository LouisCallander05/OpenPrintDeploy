using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace OpenPrintDeploy.Server.Auth;

// Route authorization alone does not protect events on an already-open page.
// Reject inbound circuit work after logout, expiry or an admin-grant change.
public sealed class AdminCircuitGuard(AuthenticationStateProvider state, AdminSession sessions,
    AdminAccessEvaluator access, AuthSchemes schemes) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        var principal = (await state.GetAuthenticationStateAsync()).User;
        if ((schemes.Admin == AdminSession.Scheme && !sessions.IsActive(principal))
            || !await access.IsAdminAsync(principal))
            throw new UnauthorizedAccessException("Admin access has ended. Sign in again.");
        await next(context);
    };
}
