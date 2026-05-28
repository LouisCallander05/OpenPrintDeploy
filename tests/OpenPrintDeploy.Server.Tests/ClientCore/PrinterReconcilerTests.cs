using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class PrinterReconcilerTests
{
    private static PrinterDto P(string unc) => new($"Name {unc}", unc);

    [Fact]
    public void AddsMissing_RemovesStale()
    {
        var desired = new SyncResponseDto([P(@"\\srv\a"), P(@"\\srv\b")]);
        var managed = new[] { @"\\srv\b", @"\\srv\c" };

        var plan = PrinterReconciler.Reconcile(desired, managed);

        Assert.Equal([@"\\srv\a"], plan.ToAdd.Select(p => p.UncPath));
        Assert.Equal([@"\\srv\c"], plan.ToRemove);
    }

    [Fact]
    public void UncComparisonIsCaseInsensitive()
    {
        var desired = new SyncResponseDto([P(@"\\SRV\A")]);
        var managed = new[] { @"\\srv\a" };

        var plan = PrinterReconciler.Reconcile(desired, managed);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void EmptyDesired_RemovesAllManaged()
    {
        var desired = new SyncResponseDto([]);
        var managed = new[] { @"\\srv\a", @"\\srv\b" };

        var plan = PrinterReconciler.Reconcile(desired, managed);

        Assert.Empty(plan.ToAdd);
        Assert.Equal(2, plan.ToRemove.Count);
    }

    [Fact]
    public void FirstRun_AddsEverything()
    {
        var desired = new SyncResponseDto([P(@"\\srv\a"), P(@"\\srv\b")]);

        var plan = PrinterReconciler.Reconcile(desired, []);

        Assert.Equal(2, plan.ToAdd.Count);
        Assert.Empty(plan.ToRemove);
    }
}
