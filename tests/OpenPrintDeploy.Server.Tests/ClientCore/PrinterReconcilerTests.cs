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

        var plan = PrinterReconciler.Reconcile(desired, managed, managed);

        Assert.Equal([@"\\srv\a"], plan.ToAdd.Select(p => p.UncPath));
        Assert.Equal([@"\\srv\c"], plan.ToRemove);
    }

    [Fact]
    public void UncComparisonIsCaseInsensitive()
    {
        var desired = new SyncResponseDto([P(@"\\SRV\A")]);
        var managed = new[] { @"\\srv\a" };

        var plan = PrinterReconciler.Reconcile(desired, managed, managed);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void AuthoritativeEmptyDesired_RemovesAllManaged()
    {
        // The user legitimately has zero printers (directory reached, no groups):
        // an authoritative empty set, so managed printers are removed.
        var desired = new SyncResponseDto([]);
        var managed = new[] { @"\\srv\a", @"\\srv\b" };

        var plan = PrinterReconciler.Reconcile(desired, managed, managed);

        Assert.Empty(plan.ToAdd);
        Assert.Equal(2, plan.ToRemove.Count);
    }

    [Fact]
    public void NonAuthoritativeEmpty_MakesNoChanges()
    {
        // Directory/server unavailable: an empty set we must NOT act on. A DC blip
        // must not uninstall a whole school's printers — make no changes at all.
        var desired = new SyncResponseDto([], Authoritative: false);
        var managed = new[] { @"\\srv\a", @"\\srv\b" };

        var plan = PrinterReconciler.Reconcile(desired, managed, managed);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void NonAuthoritative_DoesNotAddOrRemove_EvenWithPrinters()
    {
        // Defensive: a non-authoritative response is ignored wholesale, even if it
        // somehow carries printers — we only converge on confirmed state.
        var desired = new SyncResponseDto([P(@"\\srv\a")], Authoritative: false);
        var managed = new[] { @"\\srv\b" };

        var plan = PrinterReconciler.Reconcile(desired, managed, managed);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void FirstRun_AddsEverything()
    {
        var desired = new SyncResponseDto([P(@"\\srv\a"), P(@"\\srv\b")]);

        var plan = PrinterReconciler.Reconcile(desired, [], []);

        Assert.Equal(2, plan.ToAdd.Count);
        Assert.Empty(plan.ToRemove);
    }
}
