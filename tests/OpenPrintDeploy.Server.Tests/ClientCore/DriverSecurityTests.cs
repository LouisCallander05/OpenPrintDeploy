using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.AspNetCore.DataProtection;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Server.Drivers;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class DriverSecurityTests
{
    [Theory]
    [InlineData(@"\\print.school.test\Staff", true)]
    [InlineData(@"\\PRINT.SCHOOL.TEST\Staff room", true)]
    [InlineData(@"\\print.attacker.test\Staff", false)]
    [InlineData(@"\\print\Staff", false)]
    [InlineData(@"\\print.school.test\Staff\file", false)]
    [InlineData(@"\\print.school.test\..", false)]
    [InlineData("\\\\print.school.test\\Staff\0extra", false)]
    [InlineData(@"\\print.school.test\Staff:stream", false)]
    [InlineData(@"\\print.school.test@443\Staff", false)]
    public void PrivilegedSource_RequiresExactHostAndQueue(string unc, bool expected)
        => Assert.Equal(expected, DriverSourcePolicy.IsAllowed(unc, ["print.school.test"]));

    [Fact]
    public void EmptyAllowList_DeniesEverything()
        => Assert.False(DriverSourcePolicy.IsAllowed(@"\\print\Staff", []));

    [Theory]
    [InlineData("../escape.inf")]
    [InlineData(@"..\escape.inf")]
    [InlineData(@"C:\Windows\evil.inf")]
    [InlineData(@"\\server\evil.inf")]
    [InlineData("good.inf:stream")]
    [InlineData("package./driver.inf")]
    [InlineData("CON.inf")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("CON .inf")]
    [InlineData("COM¹.inf")]
    [InlineData("/absolute.inf")]
    public void PackagePaths_CannotEscapeOrUseWindowsAliases(string path)
        => Assert.Throws<InvalidDataException>(() => DriverPackageFiles.Resolve(Path.GetTempPath(), path));

    [Fact]
    public void ArchiveRejectsCaseInsensitiveDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "package.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("driver.inf");
                archive.CreateEntry("DRIVER.inf");
            }
            Assert.Throws<InvalidDataException>(() => DriverPackageFiles.Extract(path, Path.Combine(root, "files")));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task PipeRejectsOversizedOrInvalidFramesBeforeAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => DriverWire.ReadAsync<DriverRequest>(stream, default));
    }

    [Fact]
    public async Task DownloadStopsAtLimit()
    {
        using var source = new MemoryStream(new byte[100]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DriverWire.CopyLimitedAsync(source, destination, 10, default));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void TicketCannotBeTamperedWithOrUsedAcrossServers()
    {
        var tickets = new DriverTickets(new EphemeralDataProtectionProvider());
        var ticket = tickets.Issue(@"\\print\Staff");
        Assert.Equal(@"\\print\Staff", tickets.Read(ticket));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => tickets.Read("invalid" + ticket));
        var otherServer = new DriverTickets(new EphemeralDataProtectionProvider());
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => otherServer.Read(ticket));
    }

    [Fact]
    public void ExpiredTicketIsRejected()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = provider.CreateProtector("OpenPrintDeploy.DriverDownload.v1").ToTimeLimitedDataProtector();
        var ticket = protector.Protect(@"\\print\Staff", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new DriverTickets(provider).Read(ticket));
    }
}
