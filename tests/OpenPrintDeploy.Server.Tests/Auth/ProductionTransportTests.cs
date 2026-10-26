using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Auth;

public sealed class ProductionTransportTests
{
    [Fact]
    public void ProductionCannotStartWithoutHttps()
    {
        using var factory = new InsecureProductionFactory();
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Production requires enforced HTTPS", error.Message);
    }

    private sealed class InsecureProductionFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Https:Enabled"] = "false" }));
            return base.CreateHost(builder);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Production");
    }
}
