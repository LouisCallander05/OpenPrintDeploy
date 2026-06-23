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
    public void PrinterNameDiffersFromShare_NotReAdded()
    {
        // Connected via the share "\\srv\CareersShare"; Windows enumerates the
        // connection by the server's printer NAME "\\srv\Careers Printer". The
        // reconciler must treat it as installed and not re-add it every sync.
        var desired = new SyncResponseDto([new PrinterDto("Careers Printer", @"\\srv\CareersShare")]);
        var managed = new[] { @"\\srv\CareersShare" };
        var installed = new[] { @"\\srv\Careers Printer" };

        var plan = PrinterReconciler.Reconcile(desired, managed, installed);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Empty(plan.ToAdopt);
    }

    [Fact]
    public void PrinterNameDiffersFromShare_AdoptedWhenUntracked()
    {
        // Same name/share split, but OPD doesn't manage it yet — it's present by
        // its printer-name form, so adopt it (don't re-add) rather than miss it.
        var desired = new SyncResponseDto([new PrinterDto("Careers Printer", @"\\srv\CareersShare")]);
        var installed = new[] { @"\\srv\Careers Printer" };

        var plan = PrinterReconciler.Reconcile(desired, [], installed);

        Assert.Empty(plan.ToAdd);
        Assert.Equal([@"\\srv\CareersShare"], plan.ToAdopt);
    }

    [Fact]
    public void PrinterNameMatchingDoesNotResurrectAUserRemovedPrinter()
    {
        // Neither the share UNC nor the printer-name form is installed (the user
        // deleted it), so self-heal still re-adds it.
        var desired = new SyncResponseDto([new PrinterDto("Careers Printer", @"\\srv\CareersShare")]);
        var managed = new[] { @"\\srv\CareersShare" };

        var plan = PrinterReconciler.Reconcile(desired, managed, []);

        Assert.Equal([@"\\srv\CareersShare"], plan.ToAdd.Select(p => p.UncPath));
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
        Assert.Empty(plan.ToAdopt);
    }

    [Fact]
    public void DesiredPrinterAlreadyInstalledButUnmanaged_IsAdopted_NotReadded()
    {
        // A printer matching a desired UNC already exists (e.g. PaperCut left it)
        // but isn't managed. It must be adopted, never re-added.
        var desired = new SyncResponseDto([P(@"\\srv\a")]);

        var plan = PrinterReconciler.Reconcile(desired, previouslyManaged: [], currentlyInstalled: [@"\\srv\a"]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Equal([@"\\srv\a"], plan.ToAdopt);
    }

    [Fact]
    public void AlreadyManagedPrinter_IsNotAdoptedAgain()
    {
        // A printer we already manage is never re-adopted (nothing to claim).
        var desired = new SyncResponseDto([P(@"\\srv\a")]);
        var managed = new[] { @"\\srv\a" };

        var plan = PrinterReconciler.Reconcile(desired, managed, currentlyInstalled: [@"\\srv\a"]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.ToRemove);
        Assert.Empty(plan.ToAdopt);
    }

    [Fact]
    public void NonAuthoritative_AdoptsNothing()
    {
        var desired = new SyncResponseDto([P(@"\\srv\a")], Authoritative: false);

        var plan = PrinterReconciler.Reconcile(desired, [], currentlyInstalled: [@"\\srv\a"]);

        Assert.Empty(plan.ToAdopt);
    }
}
