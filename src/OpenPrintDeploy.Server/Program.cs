using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;
using OpenPrintDeploy.Server.Sync;
using OpenPrintDeploy.Server.Zones;
using OpenPrintDeploy.Shared.Sync;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("AppDb")
    ?? "Data Source=openprintdeploy.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddScoped<ZoneRepository>();
builder.Services.AddScoped<SyncHandler>();

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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/sync", async (
    SyncRequestDto request,
    SyncHandler handler,
    HttpContext http,
    CancellationToken ct) =>
{
    var response = await handler.HandleAsync(
        request,
        connectionIp: http.Connection.RemoteIpAddress,
        ct);
    return Results.Ok(response);
});

app.Run();

// Exposed for WebApplicationFactory-based tests.
public partial class Program;
