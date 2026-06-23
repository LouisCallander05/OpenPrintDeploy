using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenPrintDeploy.Server.Auth;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Auth;

public sealed class AdminAccessTests
{
    [Fact]
    public void IsOpen_OnlyWhenEmptyAndNotSealed()
    {
        Assert.True(new EffectiveAdminAccess([], [], []).IsOpen);
        Assert.False(new EffectiveAdminAccess(["Admins"], [], []).IsOpen);
        // A sealed (present-but-unreadable) store must never be open, even empty.
        Assert.False(new EffectiveAdminAccess([], [], [], Sealed: true).IsOpen);
    }

    [Fact]
    public void Read_MissingFile_IsEmptyAndReadable()
    {
        using var dir = new TempDir();
        var load = NewStore(dir).Read();
        Assert.False(load.Unreadable);
        Assert.Empty(load.Access.Groups);
        Assert.Empty(load.Access.Users);
    }

    [Fact]
    public void Read_ValidFile_ReturnsGrants()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.AdminFile, """{"Groups":["IT Admins"],"Users":["jsmith"]}""");
        var load = NewStore(dir).Read();
        Assert.False(load.Unreadable);
        Assert.Equal(["IT Admins"], load.Access.Groups);
        Assert.Equal(["jsmith"], load.Access.Users);
    }

    [Fact]
    public void Read_CorruptFile_IsUnreadable_NotOpen()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.AdminFile, "{ this is not valid json ");
        var load = NewStore(dir).Read();

        // Corrupt file: sealed, not silently re-opened.
        Assert.True(load.Unreadable);
        Assert.Empty(load.Access.Groups);
        Assert.False(new EffectiveAdminAccess([], [], [], Sealed: load.Unreadable).IsOpen);
    }

    private static AdminAccessStore NewStore(TempDir dir)
        => new(new FakeEnv(dir.Path), NullLogger<AdminAccessStore>.Instance);

    private sealed class FakeEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"opd-admin-{Guid.NewGuid():N}");

        public string AdminFile => System.IO.Path.Combine(Path, "admin-access.json");

        public TempDir() => System.IO.Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
