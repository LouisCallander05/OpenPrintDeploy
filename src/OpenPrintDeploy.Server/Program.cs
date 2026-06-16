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

// Defence in depth for the Dev (X-Dev-User header-spoof) auth scheme, which is
// only wired up in the Development environment. The one way it could leak into
// production is an installed service accidentally started with
// ASPNETCORE_ENVIRONMENT=Development — so refuse to boot in exactly that
// combination. A real dev box runs via `dotnet run`, never under the SCM.
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
    && builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Refusing to start: running as a Windows service with ASPNETCORE_ENVIRONMENT=Development. " +
        "Development mode enables header-spoof (X-Dev-User) authentication, which must never run in " +
        "production. Clear the environment variable (or set it to Production) for the installed service.");
}

// HTTPS (opt-in). When off, the existing HTTP "Urls" binding is left untouched.
// When on, we drive Kestrel's endpoints from code: HTTP stays bound (so existing
// clients keep working) and HTTPS is added with a provisioned certificate. A
// cert failure degrades to HTTP-only rather than crashing the service.
var httpsOptions = builder.Configuration.GetSection(HttpsOptions.SectionName).Get<HttpsOptions>()
    ?? new HttpsOptions();
var httpsStatus = HttpsStatus.Disabled;
var enableHsts = false;
if (httpsOptions.Enabled)
{
    using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var httpsLogger = bootstrapLoggerFactory.CreateLogger("Https");

    var certHost = ClientInstallerDownload.ResolveHost(builder.Configuration);
    var certificate = HttpsProvisioner.TryEnsureCertificate(httpsOptions, certHost, httpsLogger);

    // Enforcement only makes sense once a cert is actually bound — otherwise a
    // redirect would point HTTP clients at a dead HTTPS port.
    var enforceHttps = httpsOptions.RequireHttps && certificate is not null;
    if (httpsOptions.RequireHttps && certificate is null)
    {
        httpsLogger.LogWarning(
            "HTTPS: RequireHttps is set but no certificate was bound; staying HTTP-only without redirect.");
    }

    var selfSigned = string.IsNullOrWhiteSpace(httpsOptions.PfxPath);

    // Send HSTS only behind an operator/CA certificate. With a self-signed cert,
    // HSTS would make the browser's "untrusted certificate" warning
    // non-bypassable — locking an admin out of the UI on the very first visit
    // before they've trusted the cert. The redirect still enforces HTTPS.
    var sendHsts = enforceHttps && !selfSigned;

    httpsStatus = new HttpsStatus(
        Enabled: true,
        Bound: certificate is not null,
        SelfSigned: selfSigned,
        Host: certHost,
        Port: httpsOptions.HttpsPort,
        Thumbprint: certificate?.Thumbprint,
        Enforced: enforceHttps,
        Expiry: certificate?.NotAfter);

    if (enforceHttps)
    {
        builder.Services.AddHttpsRedirection(o => o.HttpsPort = httpsOptions.HttpsPort);
    }

    if (sendHsts)
    {
        builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(30));
        enableHsts = true;
    }

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
    // WAL + busy_timeout + synchronous=NORMAL on every connection — fleet-load
    // concurrency so logon-storm syncs serialise instead of dropping rows.
    options.AddInterceptors(new SqlitePragmaInterceptor());
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
    await BackupSqliteBeforeMigrateAsync(db, app.Logger);
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

// TLS enforcement (Https:RequireHttps with a bound cert): bounce any plain-HTTP
// request to HTTPS, before anything else handles the request. HSTS is sent only
// behind an operator/CA cert (see enableHsts) — never with a self-signed cert,
// which it would turn into a non-bypassable browser lockout.
if (httpsStatus.Enforced)
{
    if (enableHsts)
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();
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


// Hands out the tray-client MSI, pre-named "OpenPrintDeploy - <host>.msi". Wrap
// it as-is for Intune: the MSI auto-fills install/uninstall/detection on upload,
// and the tray reads the server from the filename. Admin-only.
app.MapGet("/download/client-msi", (IConfiguration cfg, IWebHostEnvironment env) =>
{
    var path = ClientInstallerDownload.ResolveMsiPath(cfg, env.ContentRootPath);
    if (!File.Exists(path))
    {
        return Results.Problem(
            detail: $"The client MSI isn't bundled with this server build (looked in '{path}'). " +
                    "Publish with scripts/Publish-Server.ps1 (it bundles the MSI), or point " +
                    "Client:MsiPath at a built OpenPrintDeploy.Client.msi.",
            statusCode: StatusCodes.Status404NotFound);
    }

    var fileName = ClientInstallerDownload.MsiDownloadFileName(cfg);
    return Results.File(path, "application/octet-stream", fileDownloadName: fileName);
})
.RequireAuthorization(AuthPolicies.Admin);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Best-effort snapshot of the SQLite database (and its WAL/SHM sidecars) before
/// migrations run, so a bad migration on the live fleet DB is recoverable. Only
/// fires when there are pending migrations against an existing on-disk database;
/// never throws — a failed backup logs and lets migration proceed.
/// </summary>
static async Task BackupSqliteBeforeMigrateAsync(AppDbContext db, ILogger logger)
{
    try
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var path = (db.Database.GetDbConnection() as Microsoft.Data.Sqlite.SqliteConnection)?.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:" || !File.Exists(path))
        {
            return; // First-ever create, or a non-file (in-memory/test) database.
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = path + suffix;
            if (File.Exists(source))
            {
                File.Copy(source, $"{source}.{stamp}.bak", overwrite: false);
            }
        }

        logger.LogInformation(
            "Backed up database to {Path}.{Stamp}.bak before applying {Count} migration(s).",
            path, stamp, pending.Count);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Pre-migration database backup failed; continuing with migration.");
    }
}

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
