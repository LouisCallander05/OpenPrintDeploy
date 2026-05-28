namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// The resolved facts about a sync request, fed to <see cref="ZoneEvaluator"/>.
/// Group resolution (transitive AD tokenGroups) happens upstream — the
/// evaluator is pure.
/// </summary>
public sealed record EvaluationContext(IReadOnlySet<string> UserGroupSids);
