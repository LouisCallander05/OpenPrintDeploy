namespace OpenPrintDeploy.Shared.Sync;

/// <param name="Printers">The resolved printer set the client should converge on.</param>
/// <param name="Authoritative">
/// Whether <paramref name="Printers"/> is a trustworthy statement of what the
/// user should have. True for a normal resolution (including a legitimately
/// empty set). False when the server could not resolve the user — e.g. the
/// directory/DC was unreachable — in which case the client must make NO changes
/// rather than tear down printers it can't confirm are unwanted. Defaults to
/// true so a normal response needs no extra ceremony; an empty body that fails
/// to deserialize is treated as non-authoritative by the client.
/// </param>
/// <param name="RemovePrinters">
/// UNC connections the server explicitly requires every client to remove,
/// including connections the client did not install or previously manage.
/// Null preserves compatibility with servers that predate this field.
/// </param>
/// <param name="SyncId">
/// Correlation ID echoed by reporting-capable servers. Null means the server is
/// older and the client should skip its best-effort result report.
/// </param>
public sealed record SyncResponseDto(
    IReadOnlyList<PrinterDto> Printers,
    bool Authoritative = true,
    IReadOnlyList<string>? RemovePrinters = null,
    Guid? SyncId = null);
