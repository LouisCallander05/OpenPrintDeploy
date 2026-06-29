using OpenPrintDeploy.Client.Core;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class PendingRemovalTests
{
    // ----- EligibleForRemoval: reconciler semantics on uninstall -----

    [Fact]
    public void EligibleForRemoval_IncludesOnlyCreatedPrinters()
    {
        var managed = new[]
        {
            new ManagedPrinter(@"\\srv\created-a", PrinterOrigin.Created),
            new ManagedPrinter(@"\\srv\adopted-b", PrinterOrigin.Adopted),
            new ManagedPrinter(@"\\srv\created-c", PrinterOrigin.Created),
        };

        var eligible = PendingRemovalPlanner.EligibleForRemoval(managed);

        Assert.Equal([@"\\srv\created-a", @"\\srv\created-c"], eligible);
    }

    [Fact]
    public void EligibleForRemoval_DropsAdoptedEvenWhenSharedUncCase()
    {
        // An adopted printer is never removed on uninstall — handed back as found.
        var managed = new[]
        {
            new ManagedPrinter(@"\\srv\keep", PrinterOrigin.Adopted),
        };

        Assert.Empty(PendingRemovalPlanner.EligibleForRemoval(managed));
    }

    [Fact]
    public void EligibleForRemoval_DeduplicatesCaseInsensitively()
    {
        var managed = new[]
        {
            new ManagedPrinter(@"\\SRV\Dup", PrinterOrigin.Created),
            new ManagedPrinter(@"\\srv\dup", PrinterOrigin.Created),
        };

        Assert.Single(PendingRemovalPlanner.EligibleForRemoval(managed));
    }

    [Fact]
    public void EligibleForRemoval_EmptyInput_EmptyResult()
        => Assert.Empty(PendingRemovalPlanner.EligibleForRemoval([]));

    // ----- Manifest round-trip + SID operations -----

    private static PendingRemoval SampleManifest() => new(
        PendingRemoval.CurrentVersion,
        "2026-06-29T00:00:00.0000000Z",
        [
            new PendingRemovalUser("S-1-5-21-1-2-3-1001", "CORP\\alice", [@"\\srv\a"]),
            new PendingRemovalUser("S-1-5-21-1-2-3-1002", "CORP\\bob", [@"\\srv\b", @"\\srv\c"]),
        ]);

    [Fact]
    public void Manifest_RoundTrips()
    {
        var parsed = PendingRemoval.Parse(SampleManifest().Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Users.Count);
        Assert.Equal(PendingRemoval.CurrentVersion, parsed.Version);
        Assert.Equal([@"\\srv\b", @"\\srv\c"], parsed.ForSid("S-1-5-21-1-2-3-1002")!.Uncs);
    }

    [Fact]
    public void Manifest_Parse_NullOrGarbage_ReturnsNull()
    {
        Assert.Null(PendingRemoval.Parse(null));
        Assert.Null(PendingRemoval.Parse("   "));
        Assert.Null(PendingRemoval.Parse("{ not json"));
    }

    [Fact]
    public void ForSid_IsCaseInsensitive_AndMissingReturnsNull()
    {
        var m = SampleManifest();

        Assert.NotNull(m.ForSid("s-1-5-21-1-2-3-1001"));
        Assert.Null(m.ForSid("S-1-5-21-9-9-9-9999"));
    }

    [Fact]
    public void WithoutSid_DropsOnlyThatUser()
    {
        var m = SampleManifest().WithoutSid("S-1-5-21-1-2-3-1001");

        Assert.Single(m.Users);
        Assert.Null(m.ForSid("S-1-5-21-1-2-3-1001"));
        Assert.NotNull(m.ForSid("S-1-5-21-1-2-3-1002"));
    }

    [Fact]
    public void WithoutSid_LastUser_LeavesEmpty()
    {
        var m = SampleManifest()
            .WithoutSid("S-1-5-21-1-2-3-1001")
            .WithoutSid("S-1-5-21-1-2-3-1002");

        Assert.Empty(m.Users);
    }
}
