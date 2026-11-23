namespace OpenPrintDeploy.Server.Admin;

public static class ClientActivityHistory
{
    public static IReadOnlyList<ClientActivityDetails> CollapseRepeatedAbsenceConfirmations(
        IEnumerable<ClientActivityDetails> activities)
    {
        var result = new List<ClientActivityDetails>();
        var latestPrinterEvent = new Dictionary<(string User, string Unc), string>();
        foreach (var activity in activities.OrderBy(a => a.OccurredAt))
        {
            if (activity.PrinterUncPath is { } unc)
            {
                var key = (activity.Username.ToUpperInvariant(), unc.Trim().ToUpperInvariant());
                // Older servers stored every no-op confirmation as a removal.
                // Recognize those generated summaries for display only; retain
                // the original audit rows and every actual removal/failure.
                var isAbsence = activity.Type == "Printer removed" &&
                    (activity.Summary.EndsWith(" was already absent because an administrator required its removal.", StringComparison.Ordinal)
                    || activity.Summary.EndsWith(" was already absent because it was no longer assigned.", StringComparison.Ordinal));
                if (isAbsence && latestPrinterEvent.GetValueOrDefault(key) == "Printer removed") continue;
                latestPrinterEvent[key] = activity.Type;
            }
            result.Add(activity);
        }
        return result.OrderByDescending(a => a.OccurredAt).ToList();
    }
}
