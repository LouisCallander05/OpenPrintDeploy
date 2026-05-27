using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Directory;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>Marker requirement for the <see cref="AuthPolicies.Admin"/> policy.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Grants admin-UI access. If no admin group SIDs are configured, any
/// authenticated user passes (single-operator/dev). Otherwise the user's
/// resolved group SIDs must intersect the configured set. Group resolution
/// happens here (the cold admin-UI path) rather than globally, so the hot
/// <c>/sync</c> path never pays for it.
/// </summary>
public sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminRequirement>
{
    private readonly IDirectoryService _directory;
    private readonly IReadOnlyList<string> _adminSids;

    public AdminAuthorizationHandler(IDirectoryService directory, IOptions<AuthOptions> options)
    {
        _directory = directory;
        _adminSids = options.Value.Admin.GroupSids;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (_adminSids.Count == 0)
        {
            context.Succeed(requirement);
            return;
        }

        var username = context.User.Identity.Name;
        if (string.IsNullOrEmpty(username))
        {
            return;
        }

        var sids = await _directory.GetGroupSidsAsync(username);
        if (sids.Overlaps(_adminSids))
        {
            context.Succeed(requirement);
        }
    }
}
