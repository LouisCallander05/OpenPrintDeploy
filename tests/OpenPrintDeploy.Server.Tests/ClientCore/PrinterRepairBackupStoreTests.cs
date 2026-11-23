using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class PrinterRepairBackupStoreTests
{
    [Fact]
    public void BackupSurvivesRestartWithRecoveryStageAndDefaultIdentity()
    {
        InTemporaryDirectory(directory =>
        {
            var snapshot = new PrinterRepairBackup(@"\\srv\queue", "Driver", @"\\srv\queue", new byte[220]) { WasDefault = true };
            var store = new FilePrinterRepairBackupStore(directory);
            using (store.Acquire(snapshot.Unc)) store.Save(snapshot);
            var restarted = new FilePrinterRepairBackupStore(directory);
            var loaded = restarted.Load(@"\\SRV\QUEUE")!;
            Assert.True(loaded.WasDefault);
            Assert.Equal(snapshot.UserDevMode, loaded.UserDevMode);
            using (restarted.Acquire(snapshot.Unc)) restarted.Save(loaded with { Reconnected = true });
            Assert.True(new FilePrinterRepairBackupStore(directory).Load(snapshot.Unc)!.Reconnected);
            Assert.Empty(System.IO.Directory.GetFiles(directory, "*.tmp"));
            using (restarted.Acquire(snapshot.Unc)) restarted.Complete(snapshot.Unc);
            Assert.Null(restarted.Load(snapshot.Unc));
            Assert.Single(System.IO.Directory.GetFiles(directory, "*.completed-*"));
        });
    }

    [Fact]
    public void SameConnectionCannotBeRepairedConcurrently()
    {
        InTemporaryDirectory(directory =>
        {
            var first = new FilePrinterRepairBackupStore(directory);
            var second = new FilePrinterRepairBackupStore(directory);
            using (first.Acquire(@"\\srv\queue"))
                Assert.Throws<IOException>(() => second.Acquire(@"\\SRV\QUEUE"));
            using var released = second.Acquire(@"\\srv\queue");
        });
    }

    [Fact]
    public void RemoteProgressSurvivesRestartAndIsIsolatedByRequestId()
    {
        InTemporaryDirectory(directory =>
        {
            var id = Guid.NewGuid();
            new FileRemoteRepairStateStore(directory).Save(id, [@"\\srv\a", @"\\srv\b"]);
            var restarted = new FileRemoteRepairStateStore(directory);
            Assert.Equal(new[] { @"\\srv\a", @"\\srv\b" }, restarted.Load(id));
            Assert.Empty(restarted.Load(Guid.NewGuid()));
        });
    }

    private static void InTemporaryDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "opd-repair-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try { test(directory); }
        finally
        {
            foreach (var file in System.IO.Directory.GetFiles(directory)) File.Delete(file);
            System.IO.Directory.Delete(directory);
        }
    }
}
