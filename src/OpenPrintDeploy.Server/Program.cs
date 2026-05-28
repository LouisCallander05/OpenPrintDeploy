using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Auth;
using OpenPrintDeploy.Server.Components;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Spooler;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

var builder = WebApplication.CreateBuilder(args);

// Lets the same exe run as a console app (dev) or under the SCM as a Windows
// service (production). No-op when not actually running as a service.
builder.Host.UseWindowsService(o => o.ServiceName = "OpenPrintDeployServer");

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

app.MapPost("/sync", async (
    SyncRequestDto request,
    SyncHandler handler,
    HttpContext http,
    CancellationToken ct) =>
{
    var username = http.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
    {
        return Results.Unauthorized();
    }

    var response = await handler.HandleAsync(username, request.MachineName, ct);
    return Results.Ok(response);
})
.RequireAuthorization()
.DisableAntiforgery();

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
