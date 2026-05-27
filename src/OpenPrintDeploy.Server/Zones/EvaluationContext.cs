using System.Net;

namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// The resolved facts about a sync request, fed to <see cref="ZoneEvaluator"/>.
/// Group resolution (transitive AD tokenGroups), OU lookup, and IP discovery
/// all happen upstream — the evaluator is pure.
/// </summary>
public sealed record EvaluationContext(
    IReadOnlySet<string> UserGroupSids,
    string? MachineOuDn,
    IPAddress? ClientIp);
