using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenPrintDeploy.Server.Auth;
using OpenPrintDeploy.Shared.Sync;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Auth;

public sealed class AdminLoginTests
{
    [Fact]
    public async Task AnonymousAdminRequest_RedirectsToForm_WithoutBrowserChallenge()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        var response = await client.GetAsync("/zones");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login?ReturnUrl=", response.Headers.Location!.ToString());
        Assert.Empty(response.Headers.WwwAuthenticate);
        var html = await client.GetStringAsync("/login");
        Assert.Contains("name=\"username\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task ValidDomainLogin_IssuesSecureSession_AndDoesNotRevalidatePasswordOnReload()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        var response = await Login(client, "/admin/directory");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/directory", response.Headers.Location!.ToString());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-OpenPrintDeploy.Admin="));
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("password", cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/zones")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/zones")).StatusCode);
        Assert.Equal(1, factory.Credentials.Calls);
    }

    [Fact]
    public async Task Login_RejectsMissingAntiforgery_WithoutValidatingCredentials()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = "DOMAIN\\admin", ["password"] = "password" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Credentials.Calls);
    }

    [Fact]
    public async Task Login_RefusesPlainHttp()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client("http://localhost");
        var html = await client.GetStringAsync("/login");
        Assert.DoesNotContain("type=\"password\"", html);
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Credentials.Calls);
    }

    [Fact]
    public async Task BadPasswords_AreThrottled_AndNeverIssueSession()
    {
        using var factory = new LoginFactory();
        factory.Credentials.Result = new(null, 1326);
        using var client = factory.Client();
        for (var i = 0; i < 5; i++)
        {
            var response = await Login(client);
            Assert.Contains("error=credentials", response.Headers.Location!.ToString());
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(v => v.StartsWith("__Host-OpenPrintDeploy.Admin=")));
        }
        var blocked = await Login(client);
        Assert.Contains("error=throttled", blocked.Headers.Location!.ToString());
        Assert.Equal(5, factory.Credentials.Calls);
    }

    [Fact]
    public async Task DomainOutage_DoesNotLockOutUser_AndRecoveryNeedsNoRestart()
    {
        using var factory = new LoginFactory();
        factory.Credentials.Result = new(null, 1311);
        using var client = factory.Client();
        for (var i = 0; i < 6; i++)
        {
            var response = await Login(client);
            Assert.Contains("error=unavailable", response.Headers.Location!.ToString());
        }
        factory.Credentials.Result = FakeCredentials.Success();
        var recovered = await Login(client);
        Assert.Equal("/", recovered.Headers.Location!.ToString());
        Assert.Equal(7, factory.Credentials.Calls);
    }

    [Fact]
    public async Task VerifiedNonAdmin_DoesNotReceiveAdminSession()
    {
        using var factory = new LoginFactory();
        factory.Credentials.Result = FakeCredentials.Success("DOMAIN\\other");
        using var client = factory.Client();
        var response = await Login(client);
        Assert.Contains("error=access", response.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/zones")).StatusCode);
    }

    [Fact]
    public async Task AdminCookie_CannotAuthenticatePrinterSync()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        await Login(client);
        var response = await client.PostAsJsonAsync("/sync", new SyncRequestDto(MachineName: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RequiresAntiforgery_ThenInvalidatesSession()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        var login = await Login(client);
        var oldCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-OpenPrintDeploy.Admin=")).Split(';')[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/zones")).StatusCode);
        var token = await Token(client);
        var response = await client.PostAsync("/auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = token }));
        Assert.Equal("/login", response.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/zones")).StatusCode);
        using var replay = factory.Client();
        replay.DefaultRequestHeaders.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Redirect, (await replay.GetAsync("/zones")).StatusCode);
    }

    [Fact]
    public async Task ExpiredSession_RequiresLoginAgain()
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        await Login(client);
        factory.Clock.Now = DateTimeOffset.UtcNow.AddHours(9);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/zones")).StatusCode);
        Assert.Equal(1, factory.Credentials.Calls);
    }

    [Theory]
    [InlineData("Forms", AdminSession.Scheme)]
    [InlineData("Basic", AdminSession.Scheme)]
    [InlineData("Negotiate", "Negotiate")]
    public async Task ExistingConfiguration_SelectsExpectedProductionScheme(string configured, string expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppAuthentication(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Auth:AdminScheme"] = configured }).Build(),
            new StoreEnvironment(Path.GetTempPath()) { EnvironmentName = "Production" });
        using var provider = services.BuildServiceProvider();
        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        Assert.Equal(expected, scheme!.Name);
        Assert.Equal("Negotiate", provider.GetRequiredService<AuthSchemes>().Client);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("//example.com")]
    [InlineData("/\\example.com")]
    [InlineData("/\r\nLocation: evil")]
    public async Task Login_RejectsExternalReturnUrls(string returnUrl)
    {
        using var factory = new LoginFactory();
        using var client = factory.Client();
        var response = await Login(client, returnUrl);
        Assert.Equal("/", response.Headers.Location!.ToString());
    }

    private static async Task<string> Token(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Assert.True(match.Success, html);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static async Task<HttpResponseMessage> Login(HttpClient client, string returnUrl = "/")
        => await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "DOMAIN\\admin", ["password"] = "password",
            ["returnUrl"] = returnUrl, ["__RequestVerificationToken"] = await Token(client),
        }));

    private sealed class FakeCredentials : IAdminCredentials
    {
        public int Calls { get; private set; }
        public AdminCredentialResult Result { get; set; } = Success();
        public static AdminCredentialResult Success(string user = "DOMAIN\\admin")
            => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], AdminSession.Scheme)));
        public Task<AdminCredentialResult> ValidateAsync(string username, string password, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class LoginFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "opd-login-" + Guid.NewGuid().ToString("N"));
        public FakeCredentials Credentials { get; } = new();
        public TestClock Clock { get; } = new();
        public HttpClient Client(string origin = "https://localhost") => CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri(origin) });
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            System.IO.Directory.CreateDirectory(_directory);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AppDb"] = $"Data Source={Path.Combine(_directory, "test.db")};Pooling=False",
                ["Https:Enabled"] = "false", ["Auth:Dev:DefaultUser"] = "",
                ["Auth:Admin:Users:0"] = "admin",
            }));
            builder.ConfigureTestServices(services =>
            {
                // Real cookie and antiforgery pipeline, isolated from Windows AD.
                // /sync retains its separate test identity scheme.
                services.AddAuthentication(options => options.DefaultScheme = AdminSession.Scheme);
                services.PostConfigure<CookieAuthenticationOptions>(AdminSession.Scheme, options => options.TimeProvider = Clock);
                services.RemoveAll<AuthSchemes>();
                services.AddSingleton(new AuthSchemes(AdminSession.Scheme, DevAuthenticationHandler.SchemeName));
                services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
                    options.AddPolicy(AuthPolicies.Admin, policy =>
                    {
                        policy.AddAuthenticationSchemes(AdminSession.Scheme);
                        policy.RequireAuthenticatedUser();
                        policy.Requirements.Add(new AdminRequirement());
                    }));
                services.RemoveAll<IAdminCredentials>();
                services.AddSingleton<IAdminCredentials>(Credentials);
                services.RemoveAll<AdminAccessStore>();
                services.AddSingleton(new AdminAccessStore(new StoreEnvironment(_directory), NullLogger<AdminAccessStore>.Instance));
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && System.IO.Directory.Exists(_directory)
                && Path.GetFullPath(_directory).StartsWith(Path.Combine(Path.GetTempPath(), "opd-login-"), StringComparison.OrdinalIgnoreCase))
                System.IO.Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class StoreEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
