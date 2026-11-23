using OpenPrintDeploy.Server.Admin;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Sync;

public sealed class ClientActivityHistoryTests
{
    [Fact]
    public void RepeatedAbsenceIsHiddenButFailuresReinstallsAndActualRemovalsAreKept()
    {
        var time = DateTimeOffset.UtcNow;
        ClientActivityDetails Activity(int minute, string type, string summary) => new(time.AddMinutes(minute), "alice",
            type, summary, null, @"\\srv\queue", null);
        var absent = @"\\srv\queue was already absent because an administrator required its removal.";
        var activities = new[]
        {
            Activity(0, "Printer removed", absent),
            Activity(1, "Printer removed", absent),
            Activity(2, "Removal failed", "Access denied"),
            Activity(3, "Printer removed", absent),
            Activity(4, "Printer installed", "Installed"),
            Activity(5, "Printer removed", "Queue was removed because an administrator required its removal."),
            Activity(6, "Printer removed", absent),
            Activity(7, "Printer removed", "Queue was removed because an administrator required its removal.")
        };
        var visible = ClientActivityHistory.CollapseRepeatedAbsenceConfirmations(activities);
        Assert.Equal(6, visible.Count);
        Assert.Equal(2, visible.Count(a => a.Summary == absent));
        Assert.Equal(2, visible.Count(a => a.Summary.Contains("was removed because")));
    }
}
