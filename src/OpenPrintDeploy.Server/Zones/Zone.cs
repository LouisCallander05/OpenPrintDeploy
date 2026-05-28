namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// A zone matches if at least one of its <see cref="Rules"/> matches the
/// evaluation context. When matched, every printer in <see cref="PrinterIds"/>
/// is added to the user's resolved set. <see cref="Priority"/> only orders
/// zones in the admin list — it does not affect evaluation.
/// </summary>
public sealed record Zone(
    Guid Id,
    string Name,
    int Priority,
    IReadOnlyList<ZoneRule> Rules,
    IReadOnlyList<Guid> PrinterIds);
