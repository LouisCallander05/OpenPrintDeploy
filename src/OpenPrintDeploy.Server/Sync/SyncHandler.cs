using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Server.Sync;

/// <summary>
/// Resolves a sync request into the printer set the client should install.
/// Identity is established by the caller (the authenticated connection); this
/// handler resolves the user's groups via the directory, runs the pure
/// <see cref="ZoneEvaluator"/>, then hydrates printer IDs to UNCs.
/// </summary>
public sealed class SyncHandler
{
    private readonly AppDbContext _db;
    private readonly ZoneRepository _zones;
    private readonly IDirectoryService _directory;
    private readonly ILogger<SyncHandler> _logger;

    public SyncHandler(
        AppDbContext db,
        ZoneRepository zones,
        IDirectoryService directory,
        ILogger<SyncHandler> logger)
    {
        _db = db;
        _zones = zones;
        _directory = directory;
        _logger = logger;
    }

    public async Task<SyncResponseDto> HandleAsync(
        ClaimsPrincipal user,
        string? machineName,
        CancellationToken ct)
    {
        var username = user.Identity?.Name ?? "(unknown)";

        // Prefer the group SIDs in the authenticated token (the Kerberos/NTLM
        // PAC). They already include the user's groups across every trusted
        // domain — the whole point when the server is joined to one domain but
        // users live in another. Fall back to a directory lookup only when the
        // token carries no groups (the dev header-auth path, which has none).
        var groupSids = PrincipalGroups.FromPrincipal(user);
        var source = "token";
        if (groupSids.Count == 0)
        {
            var resolution = await _directory.GetGroupSidsAsync(username, ct);
            source = "directory";

            // A directory outage must not be reported as "you have no printers":
            // an empty authoritative set would tell the client to uninstall
            // everything it manages. Send a non-authoritative response so the
            // client leaves the user's current printers untouched until the
            // directory recovers.
            if (!resolution.Available)
            {
                _logger.LogWarning(
                    "Sync for {User} on {Machine}: directory unavailable; returning non-authoritative response (no changes on the client).",
                    username, machineName ?? "(unknown)");
                return new SyncResponseDto([], Authoritative: false);
            }

            groupSids = resolution.Sids;
        }

        var context = new EvaluationContext(groupSids);
        var zones = await _zones.LoadAllAsync(ct);
        var result = ZoneEvaluator.Evaluate(context, zones);

        if (result.PrinterIds.Count == 0)
        {
            _logger.LogInformation(
                "Sync for {User} on {Machine}: no matching zones (groups={GroupCount}, source={Source})",
                username, machineName ?? "(unknown)", groupSids.Count, source);
            return new SyncResponseDto([]);
        }

        var printers = await _db.Printers
            .AsNoTracking()
            .Where(p => result.PrinterIds.Contains(p.Id))
            .Select(p => new PrinterDto(p.DisplayName, p.UncPath))
            .ToListAsync(ct);

        _logger.LogInformation(
            "Sync for {User} on {Machine}: returning {Count} printer(s)",
            username, machineName ?? "(unknown)", printers.Count);

        return new SyncResponseDto(printers);
    }
}
