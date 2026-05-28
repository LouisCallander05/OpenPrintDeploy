namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// Pure zone-evaluation logic. Given a request context and the set of
/// configured zones, returns the printers the client should install. Matching
/// is group-based: a zone applies when the user is a member of the group SID
/// named on at least one of its rules.
/// </summary>
public static class ZoneEvaluator
{
    public static EvaluationResult Evaluate(
        EvaluationContext context,
        IEnumerable<Zone> zones)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(zones);

        var printerIds = zones
            .Where(z => z.Rules.Any(r => RuleMatches(r, context)))
            .SelectMany(z => z.PrinterIds)
            .Distinct()
            .ToList();

        return new EvaluationResult(printerIds);
    }

    private static bool RuleMatches(ZoneRule rule, EvaluationContext ctx)
        => rule.GroupSid is { } sid && ctx.UserGroupSids.Contains(sid);
}
