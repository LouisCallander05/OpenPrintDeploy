using System.Buffers.Binary;
using System.Runtime.Versioning;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class PrinterConnectionRepairerTests
{
    private static readonly PrinterDto Printer = new("Library", @"\\srv\library");
    private static readonly PreparedDriver Driver = new("Driver", "Windows x64", 1, 2);
    private static readonly PrinterRepairBackup Snapshot = new(Printer.UncPath, Driver.Name, Printer.UncPath, new byte[220]) { WasDefault = true };

    [Fact]
    public async Task BackupAndReadinessPrecedeDisconnectAndSuccessRestoresSettings()
    {
        var events = new List<string>();
        var operations = new Operations(events);
        var backups = new Backups(events);
        await new PrinterConnectionRepairer(operations, backups).CheckAndRepairAsync(Printer, Driver, default);
        Assert.Equal(new[] { "verify", "server", "jobs", "capture", "save-original", "disconnect", "connect", "save-reconnected", "preferences", "duplex", "default" }, events);
        Assert.True(backups.Value!.Reconnected);
        Assert.Same(Snapshot.UserDevMode, backups.Value.UserDevMode);
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("server")]
    [InlineData("jobs")]
    [InlineData("save-original")]
    public async Task ReadinessOrBackupFailureLeavesConnectionUntouched(string failure)
    {
        var events = new List<string>();
        var operations = new Operations(events) { Failure = failure };
        var backups = new Backups(events) { FailSave = failure == "save-original" };
        await Assert.ThrowsAsync<IOException>(() => new PrinterConnectionRepairer(operations, backups)
            .CheckAndRepairAsync(Printer, Driver, default));
        Assert.DoesNotContain("disconnect", events);
        Assert.DoesNotContain("connect", events);
    }

    [Fact]
    public async Task PendingPrintJobsDeferWithoutCapturingOrDisconnecting()
    {
        var events = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PrinterConnectionRepairer(new Operations(events) { Jobs = true }, new Backups(events))
            .CheckAndRepairAsync(Printer, Driver, default));
        Assert.Equal(new[] { "verify", "server", "jobs" }, events);
    }

    [Fact]
    public async Task OutdatedCoreDependencyLeavesExistingConnectionAndBackupUntouched()
    {
        var events = new List<string>();
        var backups = new Backups(events);
        var id = Guid.NewGuid();
        var operations = new Operations(events)
        {
            OnServerCheck = () => CoreDriverRequirements.Verify([id], [new(id, 33822, 123)], [new(id, 33648, 123)])
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PrinterConnectionRepairer(operations, backups)
            .CheckAndRepairAsync(Printer, Driver, default));
        Assert.Equal(new[] { "verify", "server" }, events);
        Assert.Null(backups.Value);
    }

    [Fact]
    public async Task ReconnectFailureAttemptsBoundedRecoveryAndRetainsOriginalSnapshot()
    {
        var events = new List<string>();
        var backups = new Backups(events);
        await Assert.ThrowsAsync<IOException>(() => new PrinterConnectionRepairer(new Operations(events) { Failure = "connect" }, backups)
            .CheckAndRepairAsync(Printer, Driver, default));
        Assert.Equal(2, events.Count(e => e == "connect"));
        Assert.False(backups.Value!.Reconnected);
        Assert.Equal("default", events.Last());
    }

    [Fact]
    public async Task FailedPreferenceRestoreRecoversOnNextRunWithoutAnotherDisconnect()
    {
        var events = new List<string>();
        var backups = new Backups(events);
        await Assert.ThrowsAsync<IOException>(() => new PrinterConnectionRepairer(new Operations(events) { Failure = "preferences" }, backups)
            .CheckAndRepairAsync(Printer, Driver, default));
        Assert.True(backups.Value!.Reconnected);
        events.Clear();
        await new PrinterConnectionRepairer(new Operations(events), backups).CheckAndRepairAsync(Printer, Driver, default);
        Assert.DoesNotContain("disconnect", events);
        Assert.DoesNotContain("connect", events);
        Assert.Contains("preferences", events);
    }

    [Fact]
    public async Task MissingDuplexAfterReconnectIsNotReportedAsSuccessfulMigration()
    {
        var events = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PrinterConnectionRepairer(new Operations(events) { Duplex = false }, new Backups(events))
            .CheckAndRepairAsync(Printer, Driver, default));
        Assert.Equal("default", events.Last());
    }

    [Fact]
    public async Task SimplexQueuesAlsoMigrateAndDoNotRequireDuplex()
    {
        var events = new List<string>();
        await new PrinterConnectionRepairer(new Operations(events) { ServerDuplex = false, Duplex = false }, new Backups(events))
            .CheckAndRepairAsync(Printer, Driver, default);
        Assert.Contains("disconnect", events);
        Assert.DoesNotContain("duplex", events);
    }

    [Fact]
    public async Task ShutdownAfterDisconnectStillFinishesReconnectAndRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        await new PrinterConnectionRepairer(new Operations(events) { OnDisconnect = cancellation.Cancel }, new Backups(events))
            .CheckAndRepairAsync(Printer, Driver, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("default", events.Last());
        Assert.Contains("connect", events);
    }

    [Fact]
    public async Task ConnectionReaddedByNormalSyncRecoversOriginalBackupWithoutDisconnect()
    {
        var events = new List<string>();
        var backups = new Backups(events) { Value = Snapshot };
        await new PrinterConnectionRepairer(new Operations(events), backups)
            .CheckAndRepairAsync(Printer, Driver, default, connectionJustAdded: true);
        Assert.DoesNotContain("disconnect", events);
        Assert.DoesNotContain("capture", events);
        Assert.Contains("preferences", events);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreferenceMergePreservesFreshPrivateDriverDataAndOnlySupportedPublicFields()
    {
        var previous = Enumerable.Repeat((byte)3, 240).ToArray();
        var fresh = Enumerable.Repeat((byte)7, 240).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(previous.AsSpan(72), 0x1001); // orientation + duplex
        BinaryPrimitives.WriteUInt32LittleEndian(fresh.AsSpan(72), 0x1000); // only duplex supported
        WindowsPrinterRepairOperations.MergePublicPreferences(previous, fresh);
        Assert.Equal(new byte[] { 3, 3 }, fresh[94..96]);
        Assert.Equal(new byte[] { 7, 7 }, fresh[76..78]);
        Assert.All(fresh[220..], value => Assert.Equal((byte)7, value));
        Assert.All(fresh[..68], value => Assert.Equal((byte)7, value));
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32LittleEndian(fresh.AsSpan(72)));
    }

    [Fact]
    public async Task SameRemoteRequestRecoversWithoutReconnectButNewRequestForcesOne()
    {
        var events = new List<string>();
        var backups = new Backups(events);
        var repair = new PrinterConnectionRepairer(new Operations(events), backups);
        var request = Guid.NewGuid();
        await repair.CheckAndRepairAsync(Printer, Driver, default, requestId: request);
        await repair.CheckAndRepairAsync(Printer, Driver, default, requestId: request);
        await repair.CheckAndRepairAsync(Printer, Driver, default, requestId: Guid.NewGuid());
        Assert.Equal(2, events.Count(e => e == "disconnect"));
    }

    [Fact]
    public async Task AutomaticMigrationRecoversFinishedRemoteBackupWithoutAnotherReconnect()
    {
        var events = new List<string>();
        var backups = new Backups(events) { Value = Snapshot with { Reconnected = true, RequestId = Guid.NewGuid() } };
        await new PrinterConnectionRepairer(new Operations(events), backups).CheckAndRepairAsync(Printer, Driver, default);
        Assert.DoesNotContain("disconnect", events);
    }

    private sealed class Backups(List<string> events) : IPrinterRepairBackupStore
    {
        public bool FailSave { get; init; }
        public PrinterRepairBackup? Value { get; set; }
        public PrinterRepairBackup? Load(string unc) => Value;
        public void Save(PrinterRepairBackup backup)
        {
            events.Add(backup.Reconnected ? "save-reconnected" : "save-original");
            if (FailSave) throw new IOException("Backup storage unavailable");
            Value = backup;
        }
    }

    private sealed class Operations(List<string> events) : IPrinterRepairOperations
    {
        public string? Failure { get; init; }
        public bool Jobs { get; init; }
        public bool Duplex { get; init; } = true;
        public bool ServerDuplex { get; init; } = true;
        public Action? OnDisconnect { get; init; }
        public Action? OnServerCheck { get; init; }
        private void Record(string operation)
        {
            events.Add(operation);
            if (Failure == operation) throw new IOException("Simulated failure");
        }
        public void VerifyDriver(string unc, PreparedDriver driver) => Record("verify");
        public bool SupportsDuplex(string unc) { Record("duplex"); return Duplex; }
        public bool ServerDefaultsToDuplex(string unc, PreparedDriver driver) { Record("server"); OnServerCheck?.Invoke(); return ServerDuplex; }
        public bool HasPendingUserJobs(string unc) { Record("jobs"); return Jobs; }
        public PrinterRepairBackup Capture(string unc, PreparedDriver driver) { Record("capture"); return Snapshot; }
        public void Disconnect(string unc) { Record("disconnect"); OnDisconnect?.Invoke(); }
        public void Connect(string unc, PreparedDriver driver) => Record("connect");
        public void RestorePreferences(string unc, PrinterRepairBackup backup) => Record("preferences");
        public void RestoreDefault(PrinterRepairBackup backup) => Record("default");
    }
}
