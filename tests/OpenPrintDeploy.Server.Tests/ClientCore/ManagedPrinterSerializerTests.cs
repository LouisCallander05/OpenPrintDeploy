using OpenPrintDeploy.Client.Core;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class ManagedPrinterSerializerTests
{
    [Fact]
    public void RoundTrips_CurrentFormat_WithOrigin()
    {
        var managed = new[]
        {
            new ManagedPrinter(@"\\srv\a", PrinterOrigin.Created),
            new ManagedPrinter(@"\\srv\b", PrinterOrigin.Adopted),
        };

        var parsed = ManagedPrinterSerializer.Parse(ManagedPrinterSerializer.Serialize(managed));

        Assert.Equal(2, parsed.Count);
        Assert.Equal(PrinterOrigin.Created, parsed[0].Origin);
        Assert.Equal(PrinterOrigin.Adopted, parsed[1].Origin);
    }

    [Fact]
    public void Serialize_WritesOriginAsName_NotNumber()
    {
        var json = ManagedPrinterSerializer.Serialize([new ManagedPrinter(@"\\srv\a", PrinterOrigin.Adopted)]);

        Assert.Contains("Adopted", json);
        Assert.DoesNotContain("\"Origin\":1", json);
    }

    [Fact]
    public void Parse_LegacyBareStringArray_MigratesAsCreated()
    {
        // Pre-provenance format: a bare array of UNC strings.
        var parsed = ManagedPrinterSerializer.Parse("[\"\\\\srv\\\\a\", \"\\\\srv\\\\b\"]");

        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, m => Assert.Equal(PrinterOrigin.Created, m.Origin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ corrupt")]
    public void Parse_EmptyOrCorrupt_ReturnsEmpty(string? json)
        => Assert.Empty(ManagedPrinterSerializer.Parse(json));
}

public sealed class ClientPolicyTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    public void ParseRemoveOnUninstall_KnownValues(string raw, bool expected)
        => Assert.Equal(expected, ClientPolicy.ParseRemoveOnUninstall(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseRemoveOnUninstall_UnsetOrUnknown_DefaultsOn(string? raw)
    {
        Assert.True(ClientPolicy.ParseRemoveOnUninstall(raw));
        Assert.True(ClientPolicy.RemoveOnUninstallDefault);
    }
}
