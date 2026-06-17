using OpenPrintDeploy.Shared;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Shared;

public sealed class InstallerNamingTests
{
    private const string Host = "printsrv01.corp.local";
    private const string Thumb = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

    [Fact]
    public void Compose_WithThumbprint_EncodesBothTokens()
        => Assert.Equal(
            $"OpenPrintDeploy [server={Host}] [cert={Thumb}].msi",
            InstallerNaming.Compose(Host, Thumb));

    [Fact]
    public void Compose_WithoutThumbprint_EncodesServerOnly()
        => Assert.Equal($"OpenPrintDeploy [server={Host}].msi", InstallerNaming.Compose(Host, null));

    [Fact]
    public void Parse_RoundTripsComposedName()
    {
        var id = InstallerNaming.Parse(InstallerNaming.Compose(Host, Thumb));
        Assert.Equal(Host, id.Host);
        Assert.Equal(Thumb, id.Thumbprint);
    }

    [Fact]
    public void Parse_SurvivesDuplicateDownloadRename()
    {
        // The whole point of the brackets: a browser's " (1)" lands outside them.
        var id = InstallerNaming.Parse($"OpenPrintDeploy [server={Host}] [cert={Thumb}] (1).msi");
        Assert.Equal(Host, id.Host);
        Assert.Equal(Thumb, id.Thumbprint);
    }

    [Fact]
    public void Parse_FullPath_UsesFileNameOnly()
    {
        var id = InstallerNaming.Parse($@"C:\Users\admin\Downloads\OpenPrintDeploy [server={Host}] [cert={Thumb}].msi");
        Assert.Equal(Host, id.Host);
        Assert.Equal(Thumb, id.Thumbprint);
    }

    [Fact]
    public void Parse_ServerOnly_HasNoThumbprint()
    {
        var id = InstallerNaming.Parse($"OpenPrintDeploy [server={Host}].msi");
        Assert.Equal(Host, id.Host);
        Assert.Null(id.Thumbprint);
    }

    [Theory]
    [InlineData("OpenPrintDeploy - printsrv01.corp.local.msi")]
    [InlineData("OpenPrintDeploy - printsrv01.corp.local (1).msi")]
    public void Parse_LegacyDashForm_StillYieldsHost(string fileName)
    {
        var id = InstallerNaming.Parse(fileName);
        Assert.Equal(Host, id.Host);
        Assert.Null(id.Thumbprint);
    }

    [Fact]
    public void Parse_NullOrUnrecognised_ReturnsEmpty()
    {
        Assert.Equal(default, InstallerNaming.Parse(null));
        Assert.Equal(default, InstallerNaming.Parse("   "));
        Assert.Equal(default, InstallerNaming.Parse("SomeOtherInstaller.msi"));
    }
}
