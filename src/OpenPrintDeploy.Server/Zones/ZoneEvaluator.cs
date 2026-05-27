using System.Net;

namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// Pure zone-evaluation logic. Given a request context and the set of
/// configured zones, returns the printers the client should install and
/// which one (if any) to mark as default.
/// </summary>
public static class ZoneEvaluator
{
    public static EvaluationResult Evaluate(
        EvaluationContext context,
        IEnumerable<Zone> zones)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(zones);

        var matched = zones
            .Where(z => z.Rules.Any(r => RuleMatches(r, context)))
            .ToList();

        var printerIds = matched
            .SelectMany(z => z.PrinterIds)
            .Distinct()
            .ToList();

        var printerIdSet = printerIds.ToHashSet();

        // Highest-priority matched zone with a default printer wins.
        // Ties broken by zone Id for determinism.
        // Guard against a default that isn't in the resolved set (data drift).
        var defaultId = matched
            .Where(z => z.DefaultPrinterId is { } d && printerIdSet.Contains(d))
            .OrderByDescending(z => z.Priority)
            .ThenBy(z => z.Id)
            .Select(z => z.DefaultPrinterId)
            .FirstOrDefault();

        return new EvaluationResult(printerIds, defaultId);
    }

    private static bool RuleMatches(ZoneRule rule, EvaluationContext ctx)
    {
        var hasCriterion = false;

        if (rule.GroupSid is { } sid)
        {
            hasCriterion = true;
            if (!ctx.UserGroupSids.Contains(sid))
            {
                return false;
            }
        }

        if (rule.SubnetCidr is { } cidr)
        {
            hasCriterion = true;
            if (ctx.ClientIp is null)
            {
                return false;
            }
            if (!IPNetwork.TryParse(cidr, out var net))
            {
                return false;
            }
            if (!net.Contains(ctx.ClientIp))
            {
                return false;
            }
        }

        if (rule.OuDn is { } ouDn)
        {
            hasCriterion = true;
            if (ctx.MachineOuDn is null)
            {
                return false;
            }
            if (!IsSelfOrDescendantOu(ctx.MachineOuDn, ouDn))
            {
                return false;
            }
        }

        return hasCriterion;
    }

    /// <summary>
    /// True if <paramref name="machineOuDn"/> is exactly <paramref name="ruleOuDn"/>
    /// or sits underneath it in the DN tree. Comparison is case-insensitive,
    /// matching AD's behaviour. The leading comma in the suffix check prevents
    /// `OU=Sales` from spuriously matching `OU=SalesNorth`.
    /// </summary>
    private static bool IsSelfOrDescendantOu(string machineOuDn, string ruleOuDn)
    {
        var machine = NormalizeDn(machineOuDn);
        var rule = NormalizeDn(ruleOuDn);

        if (machine.Equals(rule, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return machine.EndsWith("," + rule, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDn(string dn)
    {
        // Trim whitespace around component separators. AD-DN escape sequences
        // (e.g. `\,` for a literal comma inside an RDN) are rare in OU paths
        // and aren't handled here — revisit if a customer hits it.
        return string.Join(",", dn.Split(',').Select(static p => p.Trim()));
    }
}
