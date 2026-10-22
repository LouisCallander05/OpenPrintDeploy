using System.Text.Json;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class DriverResponseTests
{
    [Fact]
    public async Task PipeCarriesVerifiedIdentity()
    {
        var driver = new PreparedDriver("HP DeskJet 3630", "Windows x64", ulong.MaxValue, 123456);
        using var stream = new MemoryStream();
        await DriverWire.WriteAsync(stream, new DriverResponse(true, Driver: driver), CancellationToken.None);
        stream.Position = 0;
        var response = await DriverWire.ReadAsync<DriverResponse>(stream, CancellationToken.None);
        Assert.True(response.Ready);
        Assert.Equal(driver, response.Driver);
    }

    [Fact]
    public void OlderServiceResponseDoesNotInventLocalDriverIdentity()
    {
        var response = JsonSerializer.Deserialize<DriverResponse>("{\"ready\":true,\"error\":null}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(response.Ready);
        Assert.Null(response.Driver);
    }
}
