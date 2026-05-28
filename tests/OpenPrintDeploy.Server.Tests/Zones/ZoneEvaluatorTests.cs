using OpenPrintDeploy.Server.Zones;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Zones;

public sealed class ZoneEvaluatorTests
{
    private static readonly Guid PrinterA = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid PrinterB = new("11111111-0000-0000-0000-000000000002");
    private static readonly Guid PrinterC = new("11111111-0000-0000-0000-000000000003");

    private const string SidEngineering = "S-1-5-21-1-2-3-1001";
    private const string SidSales = "S-1-5-21-1-2-3-1002";
    private const string SidExecs = "S-1-5-21-1-2-3-1003";

    [Fact]
    public void NoZones_ReturnsEmpty()
    {
        var result = ZoneEvaluator.Evaluate(Context([SidEngineering]), []);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void GroupRule_MatchesWhenUserHasSid()
    {
        var zone = Zone("Eng", [Rule(SidEngineering)], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context([SidEngineering]), [zone]);

        Assert.Equal([PrinterA], result.PrinterIds);
    }

    [Fact]
    public void GroupRule_DoesNotMatchWhenUserLacksSid()
    {
        var zone = Zone("Eng", [Rule(SidEngineering)], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context([SidSales]), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void Zone_MatchesIfAnyRuleMatches()
    {
        // Zone has two group rules. Either matching is enough.
        var zone = Zone("EngOrSales", [Rule(SidEngineering), Rule(SidSales)], [PrinterA]);

        Assert.Equal([PrinterA], ZoneEvaluator.Evaluate(Context([SidEngineering]), [zone]).PrinterIds);
        Assert.Equal([PrinterA], ZoneEvaluator.Evaluate(Context([SidSales]), [zone]).PrinterIds);
        Assert.Empty(ZoneEvaluator.Evaluate(Context([SidExecs]), [zone]).PrinterIds);
    }

    [Fact]
    public void EmptyCriteriaRule_NeverMatches()
    {
        // A rule with no group SID isn't treated as a wildcard — it matches nothing.
        var zone = Zone("Empty", [new ZoneRule(null)], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context([SidEngineering]), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void UnionDeduplicatesPrintersAcrossMatchedZones()
    {
        var z1 = Zone("Eng", [Rule(SidEngineering)], [PrinterA, PrinterB]);
        var z2 = Zone("Sales", [Rule(SidSales)], [PrinterB, PrinterC]);

        var result = ZoneEvaluator.Evaluate(Context([SidEngineering, SidSales]), [z1, z2]);

        Assert.Equal(3, result.PrinterIds.Count);
        Assert.Contains(PrinterA, result.PrinterIds);
        Assert.Contains(PrinterB, result.PrinterIds);
        Assert.Contains(PrinterC, result.PrinterIds);
    }

    [Fact]
    public void UnmatchedZonesContributeNothing()
    {
        var unmatched = Zone("Other", [Rule(SidExecs)], [PrinterA]);
        var matched = Zone("Eng", [Rule(SidEngineering)], [PrinterB]);

        var result = ZoneEvaluator.Evaluate(Context([SidEngineering]), [unmatched, matched]);

        Assert.Equal([PrinterB], result.PrinterIds);
    }

    // ----- helpers -----

    private static EvaluationContext Context(IEnumerable<string> groups)
        => new(groups.ToHashSet(StringComparer.Ordinal));

    private static ZoneRule Rule(string groupSid) => new(groupSid);

    private static Zone Zone(string name, IReadOnlyList<ZoneRule> rules, IReadOnlyList<Guid> printers)
        => new(Guid.NewGuid(), name, Priority: 0, rules, printers);
}
