using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Auth;
using OpenPrintDeploy.Server.Components;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Download;
using OpenPrintDeploy.Server.Https;
using OpenPrintDeploy.Server.Spooler;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Server.Updates;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

var builder = WebApplication.CreateBuilder(args);

// Lets the same exe run as a console app (dev) or under the SCM as a Windows
// service (production). No-op when not actually running as a service.
builder.Host.UseWindowsService(o => o.ServiceName = "OpenPrintDeployServer");

// HTTPS (opt-in). When off, the existing HTTP "Urls" binding is left untouched.
// When on, we drive Kestrel's endpoints from code: HTTP stays bound (so existing
// clients keep working) and HTTPS is added with a provisioned certificate. A
// cert failure degrades to HTTP-only rather than crashing the service.
var httpsOptions = builder.Configuration.GetSection(HttpsOptions.SectionName).Get<HttpsOptions>()
    ?? new HttpsOptions();
var httpsStatus = HttpsStatus.Disabled;
if (httpsOptions.Enabled)
{
    using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var httpsLogger = bootstrapLoggerFactory.CreateLogger("Https");

    var certHost = ClientInstallerDownload.ResolveHost(builder.Configuration);
    var certificate = HttpsProvisioner.TryEnsureCertificate(httpsOptions, certHost, httpsLogger);

    httpsStatus = new HttpsStatus(
        Enabled: true,
        Bound: certificate is not null,
        SelfSigned: string.IsNullOrWhiteSpace(httpsOptions.PfxPath),
        Host: certHost,
        Port: httpsOptions.HttpsPort,
        Thumbprint: certificate?.Thumbprint);

    // Take full control of binding from code; ignore the "Urls" config so we
    // don't double-bind the HTTP port.
    builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        var boundAnything = false;
        if (httpsOptions.HttpPort > 0)
        {
            kestrel.ListenAnyIP(httpsOptions.HttpPort);
            boundAnything = true;
        }

        if (certificate is not null)
        {
            kestrel.ListenAnyIP(httpsOptions.HttpsPort, listen => listen.UseHttps(certificate));
            boundAnything = true;
        }

        // Never leave the server with no listener (e.g. HTTP disabled AND the
        // cert failed) — that would make the admin UI unreachable.
        if (!boundAnything)
        {
            kestrel.ListenAnyIP(5080);
        }
    });
}

builder.Services.AddSingleton(httpsStatus);

// A context factory backs both the long-lived Blazor admin circuits
// (context-per-operation) and a scoped shim for the request-scoped sync path.
// Connection string is resolved through the DI-built IConfiguration so test
// hosts (WebApplicationFactory) can override it via in-memory sources that
// only become visible after the host is built.
builder.Services.AddDbContextFactory<AppDbContext>((sp, options) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    var cs = cfg.GetConnectionString("AppDb") ?? DefaultConnectionString(env);
    options.UseSqlite(cs, sqlite =>
        // Zones Include both Rules and Printers; split queries avoid the
        // cartesian row explosion of loading two collections in one query.
        sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
});
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddScoped<ZoneRepository>();
builder.Services.AddScoped<SyncHandler>();

builder.Services.AddScoped<PrinterService>();
builder.Services.AddScoped<ZoneService>();
builder.Services.AddScoped<SyncDiagnosticsService>();

// Spooler discovery (admin "Import from spooler"). WMI on Windows; an empty
// fallback elsewhere so dev runs on Linux/macOS still render the import panel.
builder.Services.Configure<SpoolerOptions>(
    builder.Configuration.GetSection(SpoolerOptions.SectionName));
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IPrintSpoolerService, WmiPrintSpoolerService>();
}
else
{
    builder.Services.AddSingleton<IPrintSpoolerService, StubPrintSpoolerService>();
}

// Directory provider (group resolution) — Development uses the in-memory Stub
// so tests/dev never reach for AD; Production uses LDAP against the joined
// domain. Same reasoning as auth: env is stable at startup, an Auth:Mode-style
// config read is not.
builder.Services.Configure<DirectoryOptions>(
    builder.Configuration.GetSection(DirectoryOptions.SectionName));
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IDirectoryService, StubDirectoryService>();
}
else
{
    builder.Services.AddSingleton<IDirectoryService, LdapDirectoryService>();
}

// "Check for updates" — compares this server against the latest GitHub release.
builder.Services.Configure<UpdateOptions>(
    builder.Configuration.GetSection(UpdateOptions.SectionName));
builder.Services.AddHttpClient<UpdateCheckService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
    // GitHub requires a User-Agent and recommends pinning the API version. Keep
    // the UA a fixed token (no version) so an odd version string can never throw
    // here and take down startup.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenPrintDeploy-Server");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});

builder.Services.AddAppAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (app.Environment.IsDevelopment())
    {
        await DevDataSeeder.SeedAsync(db);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// /sync authenticates clients with the client scheme (Negotiate in production,
// Dev in development) — NOT the admin Basic scheme. This keeps Kerberos clients
// working unchanged regardless of how the admin UI signs in.
var clientScheme = app.Services.GetRequiredService<AuthSchemes>().Client;
var syncPolicy = new AuthorizationPolicyBuilder(clientScheme)
    .RequireAuthenticatedUser()
    .Build();

app.MapPost("/sync", async (
    SyncRequestDto request,
    SyncHandler handler,
    HttpContext http,
    CancellationToken ct) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var response = await handler.HandleAsync(http.User, request.MachineName, ct);
    return Results.Ok(response);
})
.RequireAuthorization(syncPolicy)
.DisableAntiforgery();

// Hands out the tray-client installer, pre-named "OpenPrintDeploy - <host>.exe"
// so it configures itself against this server on first run (the installer reads
// the host out of its own filename). Admin-only, same as the dashboard.
app.MapGet("/download/client", (IConfiguration cfg, IWebHostEnvironment env) =>
{
    var path = ClientInstallerDownload.ResolveInstallerPath(cfg, env.ContentRootPath);
    if (!File.Exists(path))
    {
        return Results.Problem(
            detail: $"The client installer isn't bundled with this server build (looked in '{path}'). " +
                    "Publish with scripts/Publish-Server.ps1 (it bundles the installer), or point " +
                    "Client:InstallerPath at a built OpenPrintDeploy.Client.Installer.exe.",
            statusCode: StatusCodes.Status404NotFound);
    }

    var fileName = ClientInstallerDownload.DownloadFileName(cfg);
    return Results.File(path, "application/octet-stream", fileDownloadName: fileName);
})
.RequireAuthorization(AuthPolicies.Admin);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Where SQLite lives when the operator hasn't pinned <c>ConnectionStrings:AppDb</c>.
/// In dev (or off Windows) it's a file in the working directory so the repo
/// behaviour is unchanged; under the service install on Windows it's a stable
/// path in <c>ProgramData</c> so the DB survives upgrades and reinstalls.
/// </summary>
static string DefaultConnectionString(IHostEnvironment env)
{
    if (env.IsDevelopment() || !OperatingSystem.IsWindows())
    {
        return "Data Source=openprintdeploy.db";
    }

    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenPrintDeploy");
    Directory.CreateDirectory(dataDir);
    return $"Data Source={Path.Combine(dataDir, "app.db")}";
}

// Exposed for WebApplicationFactory-based tests.
public partial class Program;
