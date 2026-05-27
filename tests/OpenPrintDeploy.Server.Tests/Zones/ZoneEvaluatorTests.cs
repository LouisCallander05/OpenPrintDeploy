using System.Net;
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
        var ctx = Context(groups: [SidEngineering]);

        var result = ZoneEvaluator.Evaluate(ctx, []);

        Assert.Empty(result.PrinterIds);
        Assert.Null(result.DefaultPrinterId);
    }

    [Fact]
    public void GroupRule_MatchesWhenUserHasSid()
    {
        var zone = Zone("Eng", [Rule(group: SidEngineering)], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [zone]);

        Assert.Equal([PrinterA], result.PrinterIds);
    }

    [Fact]
    public void GroupRule_DoesNotMatchWhenUserLacksSid()
    {
        var zone = Zone("Eng", [Rule(group: SidEngineering)], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidSales]), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void Rule_AllNonNullCriteriaMustMatch()
    {
        // Rule needs BOTH the group AND the subnet to match.
        var zone = Zone("EngOnLan",
            [Rule(group: SidEngineering, subnet: "10.0.0.0/24")],
            [PrinterA]);

        var groupOnly = Context(groups: [SidEngineering], ip: "192.168.1.5");
        var both = Context(groups: [SidEngineering], ip: "10.0.0.5");
        var ipOnly = Context(groups: [SidSales], ip: "10.0.0.5");

        Assert.Empty(ZoneEvaluator.Evaluate(groupOnly, [zone]).PrinterIds);
        Assert.Equal([PrinterA], ZoneEvaluator.Evaluate(both, [zone]).PrinterIds);
        Assert.Empty(ZoneEvaluator.Evaluate(ipOnly, [zone]).PrinterIds);
    }

    [Fact]
    public void Zone_MatchesIfAnyRuleMatches()
    {
        // Zone has two rules. Either matching is enough.
        var zone = Zone("EngOrSales",
            [Rule(group: SidEngineering), Rule(group: SidSales)],
            [PrinterA]);

        Assert.Equal([PrinterA],
            ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [zone]).PrinterIds);
        Assert.Equal([PrinterA],
            ZoneEvaluator.Evaluate(Context(groups: [SidSales]), [zone]).PrinterIds);
        Assert.Empty(
            ZoneEvaluator.Evaluate(Context(groups: [SidExecs]), [zone]).PrinterIds);
    }

    [Fact]
    public void EmptyCriteriaRule_NeverMatches()
    {
        // A rule with no criteria isn't treated as a wildcard — it matches nothing.
        var zone = Zone("Empty", [new ZoneRule(null, null, null)], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void SubnetRule_RequiresClientIp()
    {
        var zone = Zone("Lan", [Rule(subnet: "10.0.0.0/24")], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(ip: null), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("10.0.0.255", true)]
    [InlineData("10.0.1.5", false)]
    public void SubnetRule_IPv4Matching(string ip, bool shouldMatch)
    {
        var zone = Zone("Lan", [Rule(subnet: "10.0.0.0/24")], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(ip: ip), [zone]);

        Assert.Equal(shouldMatch ? new[] { PrinterA } : [], result.PrinterIds);
    }

    [Theory]
    [InlineData("2001:db8::1", true)]
    [InlineData("2001:db8:0:0:ffff::1", true)]
    [InlineData("2001:db9::1", false)]
    public void SubnetRule_IPv6Matching(string ip, bool shouldMatch)
    {
        var zone = Zone("V6", [Rule(subnet: "2001:db8::/32")], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(ip: ip), [zone]);

        Assert.Equal(shouldMatch ? new[] { PrinterA } : [], result.PrinterIds);
    }

    [Fact]
    public void SubnetRule_MalformedCidrFailsClosed()
    {
        var zone = Zone("Bad", [Rule(subnet: "not-a-cidr")], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(ip: "10.0.0.5"), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void OuRule_RequiresMachineOu()
    {
        var zone = Zone("OuOnly", [Rule(ou: "OU=Sales,DC=corp,DC=local")], [PrinterA]);

        var result = ZoneEvaluator.Evaluate(Context(machineOu: null), [zone]);

        Assert.Empty(result.PrinterIds);
    }

    [Fact]
    public void OuRule_ExactMatch()
    {
        var zone = Zone("Sales", [Rule(ou: "OU=Sales,DC=corp,DC=local")], [PrinterA]);
        var ctx = Context(machineOu: "OU=Sales,DC=corp,DC=local");

        Assert.Equal([PrinterA], ZoneEvaluator.Evaluate(ctx, [zone]).PrinterIds);
    }

    [Fact]
    public void OuRule_DescendantMatch()
    {
        var zone = Zone("Sales", [Rule(ou: "OU=Sales,DC=corp,DC=local")], [PrinterA]);
        var ctx = Context(machineOu: "OU=Workstations,OU=Sales,DC=corp,DC=local");

        Assert.Equal([PrinterA], ZoneEvaluator.Evaluate(ctx, [zone]).PrinterIds);
    }

    [Fact]
    public void OuRule_DoesNotMatchSiblingOu()
    {
        var zone = Zone("Sales", [Rule(ou: "OU=Sales,DC=corp,DC=local")], [PrinterA]);
        var ctx = Context(machineOu: "OU=Engineering,DC=corp,DC=local");

        Assert.Empty(ZoneEvaluator.Evaluate(ctx, [zone]).PrinterIds);
    }

    [Fact]
    public void OuRule_NoSubstringFalsePositive()
    {
        // OU=Sales must not match OU=SalesNorth — that's a sibling, not a child.
        var zone = Zone("Sales", [Rule(ou: "OU=Sales,DC=corp,DC=local")], [PrinterA]);
        var ctx = Context(machineOu: "OU=SalesNorth,DC=corp,DC=local");

        Assert.Empty(ZoneEvaluator.Evaluate(ctx, [zone]).PrinterIds);
    }

    [Fact]
    public void OuRule_CaseInsensitive()
    {
        var zone = Zone("Sales", [Rule(ou: "OU=Sales,DC=corp,DC=local")], [PrinterA]);
        var ctx = Context(machineOu: "ou=workstations,Ou=sales,dc=CORP,dc=local");

        Assert.Equal([PrinterA], ZoneEvaluator.Evaluate(ctx, [zone]).PrinterIds);
    }

    [Fact]
    public void UnionDeduplicatesPrintersAcrossMatchedZones()
    {
        var z1 = Zone("Eng", [Rule(group: SidEngineering)], [PrinterA, PrinterB]);
        var z2 = Zone("Lan", [Rule(subnet: "10.0.0.0/24")], [PrinterB, PrinterC]);

        var ctx = Context(groups: [SidEngineering], ip: "10.0.0.5");
        var result = ZoneEvaluator.Evaluate(ctx, [z1, z2]);

        Assert.Equal(3, result.PrinterIds.Count);
        Assert.Contains(PrinterA, result.PrinterIds);
        Assert.Contains(PrinterB, result.PrinterIds);
        Assert.Contains(PrinterC, result.PrinterIds);
    }

    [Fact]
    public void Default_PicksHighestPriorityMatchedZoneWithDefault()
    {
        var low = Zone("Lo", [Rule(group: SidEngineering)], [PrinterA], priority: 10, defaultId: PrinterA);
        var high = Zone("Hi", [Rule(group: SidEngineering)], [PrinterB], priority: 50, defaultId: PrinterB);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [low, high]);

        Assert.Equal(PrinterB, result.DefaultPrinterId);
    }

    [Fact]
    public void Default_SkipsHigherPriorityZoneWithoutDefault()
    {
        var lowWithDefault = Zone("Lo", [Rule(group: SidEngineering)], [PrinterA], priority: 10, defaultId: PrinterA);
        var highNoDefault = Zone("Hi", [Rule(group: SidEngineering)], [PrinterB], priority: 50, defaultId: null);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [lowWithDefault, highNoDefault]);

        Assert.Equal(PrinterA, result.DefaultPrinterId);
    }

    [Fact]
    public void Default_IgnoresUnmatchedZonesDefault()
    {
        var unmatched = Zone("Other", [Rule(group: SidExecs)], [PrinterA], priority: 100, defaultId: PrinterA);
        var matched = Zone("Eng", [Rule(group: SidEngineering)], [PrinterB], priority: 1, defaultId: PrinterB);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [unmatched, matched]);

        Assert.Equal([PrinterB], result.PrinterIds);
        Assert.Equal(PrinterB, result.DefaultPrinterId);
    }

    [Fact]
    public void Default_IsNullIfNoMatchedZoneSetsOne()
    {
        var zone = Zone("Eng", [Rule(group: SidEngineering)], [PrinterA], defaultId: null);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [zone]);

        Assert.Equal([PrinterA], result.PrinterIds);
        Assert.Null(result.DefaultPrinterId);
    }

    [Fact]
    public void Default_GuardsAgainstDefaultNotInPrinterSet()
    {
        // Data drift: zone designates a default that isn't in its own printer
        // list. Treat as no default rather than returning a phantom.
        var zone = Zone("Eng", [Rule(group: SidEngineering)], [PrinterA], defaultId: PrinterC);

        var result = ZoneEvaluator.Evaluate(Context(groups: [SidEngineering]), [zone]);

        Assert.Equal([PrinterA], result.PrinterIds);
        Assert.Null(result.DefaultPrinterId);
    }

    [Fact]
    public void Default_TiePriorityBreaksByZoneId()
    {
        // Same priority — lower Guid wins deterministically.
        var zoneLowId = new Zone(
            Id: new Guid("00000000-0000-0000-0000-000000000001"),
            Name: "A",
            Priority: 10,
            Rules: [Rule(group: SidEngineering)],
            PrinterIds: [PrinterA],
            DefaultPrinterId: PrinterA);
        var zoneHighId = new Zone(
            Id: new Guid("00000000-0000-0000-0000-000000000002"),
            Name: "B",
            Priority: 10,
            Rules: [Rule(group: SidEngineering)],
            PrinterIds: [PrinterB],
            DefaultPrinterId: PrinterB);

        var result = ZoneEvaluator.Evaluate(
            Context(groups: [SidEngineering]),
            [zoneHighId, zoneLowId]);  // input order shouldn't matter

        Assert.Equal(PrinterA, result.DefaultPrinterId);
    }

    // ----- helpers -----

    private static EvaluationContext Context(
        IEnumerable<string>? groups = null,
        string? machineOu = null,
        string? ip = "10.0.0.5")
    {
        return new EvaluationContext(
            UserGroupSids: (groups ?? []).ToHashSet(StringComparer.Ordinal),
            MachineOuDn: machineOu,
            ClientIp: ip is null ? null : IPAddress.Parse(ip));
    }

    private static ZoneRule Rule(
        string? group = null,
        string? subnet = null,
        string? ou = null)
        => new(group, subnet, ou);

    private static Zone Zone(
        string name,
        IReadOnlyList<ZoneRule> rules,
        IReadOnlyList<Guid> printers,
        int priority = 0,
        Guid? defaultId = null)
        => new(Guid.NewGuid(), name, priority, rules, printers, defaultId);
}
