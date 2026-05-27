using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Admin;
using OpenPrintDeploy.Server.Auth;
using OpenPrintDeploy.Server.Components;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Directory;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("AppDb")
    ?? "Data Source=openprintdeploy.db";

// A context factory backs both the long-lived Blazor admin circuits
// (context-per-operation) and a scoped shim for the request-scoped sync path.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(connectionString, sqlite =>
        // Zones Include both Rules and Printers; split queries avoid the
        // cartesian row explosion of loading two collections in one query.
        sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddScoped<ZoneRepository>();
builder.Services.AddScoped<SyncHandler>();

builder.Services.AddScoped<PrinterService>();
builder.Services.AddScoped<ZoneService>();

// Directory provider (group + OU resolution), chosen by config.
builder.Services.Configure<DirectoryOptions>(
    builder.Configuration.GetSection(DirectoryOptions.SectionName));
var directoryProvider = builder.Configuration[$"{DirectoryOptions.SectionName}:Provider"] ?? "Stub";
if (directoryProvider.Equals("Ldap", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDirectoryService, LdapDirectoryService>();
}
else
{
    builder.Services.AddSingleton<IDirectoryService, StubDirectoryService>();
}

builder.Services.AddAppAuthentication(builder.Configuration);

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

    var response = await handler.HandleAsync(
        username,
        request.MachineName,
        connectionIp: http.Connection.RemoteIpAddress,
        ct);
    return Results.Ok(response);
})
.RequireAuthorization()
.DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposed for WebApplicationFactory-based tests.
public partial class Program;
