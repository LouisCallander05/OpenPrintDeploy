using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Server.Admin;

/// <summary>
/// Explains, for one username, what a <c>/sync</c> would resolve: the groups the
/// directory returns, which zones those groups match, and the printers that
/// would deploy. This is the answer to "authenticated but 0 printers" — it
/// surfaces the same group resolution the real sync uses, so an admin can see
/// whether the directory returned nothing (a lookup problem) or the user's
/// groups simply don't match any zone.
/// </summary>
public sealed class SyncDiagnosticsService
{
    private readonly ZoneService _zones;
    private readonly IDirectoryService _directory;

    public SyncDiagnosticsService(ZoneService zones, IDirectoryService directory)
    {
        _zones = zones;
        _directory = directory;
    }

    public async Task<SyncDiagnostics> PreviewAsync(string username, CancellationToken ct = default)
    {
        var account = DirectoryUsername.Normalize(username);

        // Same call the real sync makes — so a failure here reproduces the bug.
        var groupSids = await _directory.GetGroupSidsAsync(username, ct);

        var groups = new List<ResolvedGroup>(groupSids.Count);
        foreach (var sid in groupSids.OrderBy(s => s, StringComparer.Ordinal))
        {
            groups.Add(new ResolvedGroup(sid, await _directory.ResolveGroupNameAsync(sid, ct)));
        }

        // Mirror ZoneEvaluator: a zone matches when any of its rule SIDs is one
        // of the user's group SIDs; printers come from the matched zones.
        var zones = await _zones.GetAllAsync(ct);
        var matched = new List<MatchedZone>();
        var printers = new Dictionary<Guid, PrinterDto>();
        foreach (var zone in zones)
        {
            var hits = zone.Rules
                .Where(r => !string.IsNullOrWhiteSpace(r.GroupSid) && groupSids.Contains(r.GroupSid!))
                .Select(r => r.GroupSid!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            matched.Add(new MatchedZone(zone.Name, hits));
            foreach (var printer in zone.Printers)
            {
                printers[printer.Id] = new PrinterDto(printer.DisplayName, printer.UncPath);
            }
        }

        return new SyncDiagnostics(account, groups, matched, printers.Values.ToList());
    }
}

/// <summary>One of the user's resolved groups: its SID and (if resolvable) name.</summary>
public sealed record ResolvedGroup(string Sid, string? Name);

/// <summary>A zone the user matched, and which of its rule SIDs did the matching.</summary>
public sealed record MatchedZone(string Name, IReadOnlyList<string> MatchedSids);

/// <summary>What a sync would resolve for a username — the diagnostic view.</summary>
public sealed record SyncDiagnostics(
    string NormalizedAccount,
    IReadOnlyList<ResolvedGroup> Groups,
    IReadOnlyList<MatchedZone> MatchedZones,
    IReadOnlyList<PrinterDto> Printers);
