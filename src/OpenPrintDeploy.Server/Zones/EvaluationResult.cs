namespace OpenPrintDeploy.Server.Zones;

public sealed record EvaluationResult(
    IReadOnlyList<Guid> PrinterIds,
    Guid? DefaultPrinterId);
