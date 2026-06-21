using OpenPrintDeploy.Shared;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Shared;

public sealed class InstallerNamingTests
{
    private const string Host = "printsrv01.corp.local";
    private const string Thumb = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

    [Fact]
    public void Compose_WithThumbprint_UsesDashSeparators()
        => Assert.Equal(
            $"OpenPrintDeploy - {Host} - {Thumb}.msi",
            InstallerNaming.Compose(Host, Thumb));

    [Fact]
    public void Compose_WithoutThumbprint_MatchesLegacyHostOnlyName()
        => Assert.Equal($"OpenPrintDeploy - {Host}.msi", InstallerNaming.Compose(Host, null));

    [Fact]
    public void Compose_NeverEmitsWildcardCharacters()
    {
        // [ and ] break IntuneWinAppUtil and other path-globbing tools.
        var name = InstallerNaming.Compose(Host, Thumb);
        Assert.DoesNotContain('[', name);
        Assert.DoesNotContain(']', name);
    }

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
        var id = InstallerNaming.Parse($"OpenPrintDeploy - {Host} - {Thumb} (1).msi");
        Assert.Equal(Host, id.Host);
        Assert.Equal(Thumb, id.Thumbprint);
    }

    [Fact]
    public void Parse_FullPath_UsesFileNameOnly()
    {
        var id = InstallerNaming.Parse($@"C:\Users\admin\Downloads\OpenPrintDeploy - {Host} - {Thumb}.msi");
        Assert.Equal(Host, id.Host);
        Assert.Equal(Thumb, id.Thumbprint);
    }

    [Theory]
    [InlineData("OpenPrintDeploy - printsrv01.corp.local.msi")]
    [InlineData("OpenPrintDeploy - printsrv01.corp.local (1).msi")]
    public void Parse_HostOnly_HasNoThumbprint(string fileName)
    {
        var id = InstallerNaming.Parse(fileName);
        Assert.Equal(Host, id.Host);
        Assert.Null(id.Thumbprint);
    }

    [Fact]
    public void Parse_LegacyBracketForm_StillUnderstood()
    {
        // A client installed from a v0.9.5 bracket-named MSI must still resolve.
        var id = InstallerNaming.Parse($"OpenPrintDeploy [server={Host}] [cert={Thumb}].msi");
        Assert.Equal(Host, id.Host);
        Assert.Equal(Thumb, id.Thumbprint);
    }

    [Fact]
    public void Parse_NullOrUnrecognised_ReturnsEmpty()
    {
        Assert.Equal(default, InstallerNaming.Parse(null));
        Assert.Equal(default, InstallerNaming.Parse("   "));
        Assert.Equal(default, InstallerNaming.Parse("SomeOtherInstaller.msi"));
    }
}
