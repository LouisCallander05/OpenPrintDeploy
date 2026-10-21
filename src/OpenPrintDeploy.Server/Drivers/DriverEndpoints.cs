using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data;

namespace OpenPrintDeploy.Server.Drivers;

public sealed record PackageRequest(string Ticket, string? Sha256 = null);

public static class DriverEndpoints
{
    public static void MapDriverEndpoints(this WebApplication app)
    {
        // A short-lived capability from authenticated /sync authorizes these
        // requests. SYSTEM doesn't need the user's password or a domain account.
        foreach (var download in new[] { false, true })
        {
            var isDownload = download;
            app.MapPost(isDownload ? "/drivers/package" : "/drivers/manifest", async (
                PackageRequest request, DriverTickets tickets, DriverPackages packages,
                AppDbContext db, HttpContext http, CancellationToken ct) =>
            {
                if (!http.Request.IsHttps) return Results.BadRequest("Driver delivery requires HTTPS.");
                string unc;
                try
                {
                    if (string.IsNullOrEmpty(request.Ticket) || request.Ticket.Length > 8192) return Results.Unauthorized();
                    unc = tickets.Read(request.Ticket);
                }
                catch (CryptographicException) { return Results.Unauthorized(); }
                if (!await db.Printers.AnyAsync(p => p.UncPath == unc, ct)
                    || await db.RemovedPrinters.AnyAsync(p => p.UncPath == unc, ct)) return Results.NotFound();
                try
                {
                    var package = await packages.GetAsync(unc, ct);
                    if (!isDownload) return Results.Ok(package.Manifest);
                    if (!string.Equals(request.Sha256, package.Manifest.Sha256, StringComparison.Ordinal))
                        return Results.Conflict("Driver changed during download; retry sync.");
                    return Results.File(File.OpenRead(package.Zip), "application/zip");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    app.Logger.LogWarning(ex, "Driver delivery failed for {Queue}", unc);
                    return Results.Problem(detail: ex.Message, statusCode: 503);
                }
            }).AllowAnonymous().RequireRateLimiting("drivers")
                .WithMetadata(new RequestSizeLimitAttribute(16384)).DisableAntiforgery();
        }
    }
}
