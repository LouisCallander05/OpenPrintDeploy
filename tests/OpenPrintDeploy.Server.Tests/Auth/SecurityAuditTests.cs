using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Auth;
using OpenPrintDeploy.Server.Directory;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Auth;

public sealed class SecurityAuditTests
{
    private static ClaimsPrincipal User(string name, params Claim[] claims)
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }.Concat(claims), AdminSession.Scheme));

    private static AdminAccessEvaluator Evaluator(params string[] users)
    {
        var options = new AuthOptions();
        options.Admin.Users.AddRange(users);
        var env = new TestEnvironment();
        var store = new AdminAccessStore(env, NullLogger<AdminAccessStore>.Instance);
        return new(new StubDirectoryService(Options.Create(new DirectoryOptions())), store, Options.Create(options));
    }

    [Fact]
    public async Task UserGrant_DoesNotCrossDomainsOrAcceptBareNameAlias()
    {
        var access = new EffectiveAdminAccess([], ["SCHOOL\\admin"], []);
        var evaluator = Evaluator();
        Assert.True(await evaluator.IsAdminAsync(User("school\\ADMIN"), access));
        Assert.False(await evaluator.IsAdminAsync(User("OTHER\\admin"), access));
        Assert.False(await evaluator.IsAdminAsync(User("admin@other.example"), access));
        Assert.False(await evaluator.IsAdminAsync(User("SCHOOL\\admin"), new EffectiveAdminAccess([], ["admin"], [])));
    }

    [Fact]
    public async Task Bootstrap_RequiresServerAdministratorToken()
    {
        var evaluator = Evaluator();
        var empty = new EffectiveAdminAccess([], [], []);
        Assert.False(await evaluator.IsAdminAsync(User("SCHOOL\\student"), empty));
        Assert.True(await evaluator.IsAdminAsync(User("SCHOOL\\admin", new Claim(ClaimTypes.GroupSid, "S-1-5-32-544")), empty));
        Assert.False(await evaluator.IsAdminAsync(User("SCHOOL\\student", new Claim(ClaimTypes.DenyOnlySid, "S-1-5-32-544")), empty));
    }

    [Fact]
    public async Task UserSidGrant_DoesNotMatchGroupSid()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var access = new EffectiveAdminAccess([], [sid], []);
        Assert.True(await Evaluator().IsAdminAsync(User("SCHOOL\\admin", new Claim(ClaimTypes.PrimarySid, sid)), access));
        Assert.False(await Evaluator().IsAdminAsync(User("SCHOOL\\other", new Claim(ClaimTypes.GroupSid, sid)), access));
    }

    [Fact]
    public async Task Circuit_RejectsLogoutRevocationBeforeInvokingNextEvent()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new AdminSession(cache);
        var principal = User("SCHOOL\\admin");
        var key = await sessions.StoreAsync(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, AdminSession.Scheme));
        var guard = new AdminCircuitGuard(new TestState(principal), sessions, Evaluator("SCHOOL\\admin"),
            new AuthSchemes(AdminSession.Scheme, "Negotiate"));
        var events = 0;
        var dispatch = guard.CreateInboundActivityHandler(_ => { events++; return Task.CompletedTask; });
        await dispatch(null!);
        Assert.Equal(1, events);
        await sessions.RemoveAsync(key);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatch(null!));
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Circuit_RejectsActiveSessionWithoutCurrentAdminGrant()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new AdminSession(cache);
        var principal = User("SCHOOL\\formeradmin");
        await sessions.StoreAsync(new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, AdminSession.Scheme));
        var guard = new AdminCircuitGuard(new TestState(principal), sessions, Evaluator("SCHOOL\\currentadmin"),
            new AuthSchemes(AdminSession.Scheme, "Negotiate"));
        var invoked = false;
        var dispatch = guard.CreateInboundActivityHandler(_ => { invoked = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => dispatch(null!));
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(true, true, "Negotiate")]
    [InlineData(false, false, "Basic")]
    public void Directory_RejectsInsecureTransportConfiguration(bool ssl, bool invalidCert, string mode)
    {
        var options = new DirectoryOptions();
        options.Ldap.UseSsl = ssl;
        options.Ldap.AllowInvalidCertificate = invalidCert;
        options.Ldap.AuthMode = mode;
        Assert.Throws<InvalidOperationException>(() => new LdapDirectoryService(Options.Create(options),
            NullLogger<LdapDirectoryService>.Instance));
    }

    private sealed class TestState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        // No files are created. This unique missing directory represents first run.
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "opd-audit-" + Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
