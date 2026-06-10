using Microsoft.AspNetCore.Authorization;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>Marker requirement for the <see cref="AuthPolicies.Admin"/> policy.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Grants admin-UI access by delegating to <see cref="AdminAccessEvaluator"/>,
/// which combines the appsettings break-glass grants with the editable
/// Settings-page list (and falls back to "any authenticated user" when nothing
/// is configured anywhere). Runs on the cold admin-UI path only.
/// </summary>
public sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminRequirement>
{
    private readonly AdminAccessEvaluator _evaluator;

    public AdminAuthorizationHandler(AdminAccessEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (await _evaluator.IsAdminAsync(context.User))
        {
            context.Succeed(requirement);
        }
    }
}
