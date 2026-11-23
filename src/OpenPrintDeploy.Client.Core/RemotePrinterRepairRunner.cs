using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

public interface IRemoteRepairStateStore
{
    IReadOnlyCollection<string> Load(Guid requestId);
    void Save(Guid requestId, IReadOnlyCollection<string> completed);
}

public sealed record RemoteRepairOutcome(IReadOnlyCollection<string> Completed, IReadOnlyList<PrinterApplyError> Failures);

public sealed class RemotePrinterRepairRunner(SyncApiClient api, IPrinterConnectionRepairer repairer, IRemoteRepairStateStore state)
{
    public async Task<RemoteRepairOutcome> RunAsync(Guid requestId, string deviceId,
        IReadOnlyList<PrinterDto> printers, IReadOnlyDictionary<string, PreparedDriver> drivers,
        IReadOnlyList<PrinterApplyError> existingFailures, IReadOnlyCollection<string> justAdded, CancellationToken ct)
    {
        await ReportAsync(new(requestId, deviceId, PrinterRepairStatus.Running), ct);
        HashSet<string> completed;
        try { completed = state.Load(requestId).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = "Could not read local repair progress: " + ex.Message;
            await ReportAsync(new(requestId, deviceId, PrinterRepairStatus.Failed, Limit(message)), ct);
            return new([], printers.DistinctBy(p => p.UncPath, StringComparer.OrdinalIgnoreCase)
                .Where(p => !existingFailures.Any(f => f.Printer.UncPath.Equals(p.UncPath, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new PrinterApplyError(p, message)).ToList());
        }
        var errors = new List<PrinterApplyError>();
        var additionalFailures = new List<PrinterApplyError>();
        foreach (var printer in printers.DistinctBy(p => p.UncPath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (completed.Contains(printer.UncPath)) continue;
            var blocked = existingFailures.FirstOrDefault(f => f.Printer.UncPath.Equals(printer.UncPath, StringComparison.OrdinalIgnoreCase));
            if (blocked is not null) { errors.Add(blocked); continue; }
            try
            {
                if (!drivers.TryGetValue(printer.UncPath, out var driver))
                    throw new InvalidOperationException("Waiting for verified driver preparation.");
                await repairer.CheckAndRepairAsync(printer, driver, ct,
                    justAdded.Contains(printer.UncPath, StringComparer.OrdinalIgnoreCase), requestId);
                // Save before acknowledging completion or advancing to another
                // queue. Native recovery also persists its reconnect stage.
                var next = completed.Append(printer.UncPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                state.Save(requestId, next);
                completed.Add(printer.UncPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = new PrinterApplyError(printer, "Remote repair failed: " + ex.Message);
                errors.Add(failure);
                additionalFailures.Add(failure);
            }
        }
        await ReportAsync(new(requestId, deviceId,
            errors.Count == 0 ? PrinterRepairStatus.Completed : PrinterRepairStatus.Failed,
            errors.Count == 0 ? null : Limit(string.Join("; ", errors.Select(f => $"{f.Printer.DisplayName}: {f.Message}")))), ct);
        return new(printers.Where(p => completed.Contains(p.UncPath)).Select(p => p.UncPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), additionalFailures);
    }

    private static string Limit(string message) => message[..Math.Min(message.Length, 1024)];

    private async Task ReportAsync(PrinterRepairReportDto report, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await api.ReportRepairAsync(report, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (HttpRequestException) { }
    }
}
