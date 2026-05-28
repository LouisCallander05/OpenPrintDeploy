using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data.Entities;

namespace OpenPrintDeploy.Server.Data;

/// <summary>
/// Inserts a tiny demo dataset on first run so the server has something to
/// respond with. Skipped if any zones already exist. Dev-only — production
/// deployments will configure via the admin UI.
/// </summary>
public static class DevDataSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Skip if anything's already in the DB, not just zones — otherwise a
        // dev who's emptied zones via the UI hits a UNC-uniqueness crash on
        // restart when the seeder tries to re-insert the demo printers.
        if (await db.Zones.AnyAsync(ct) || await db.Printers.AnyAsync(ct))
        {
            return;
        }

        var hrMfp = new PrinterEntity
        {
            UncPath = @"\\printsrv01\HR-MFP-01",
            DisplayName = "HR Multifunction (Floor 2)",
        };
        var engColor = new PrinterEntity
        {
            UncPath = @"\\printsrv01\ENG-Color-01",
            DisplayName = "Engineering Colour",
        };
        var lobbyMono = new PrinterEntity
        {
            UncPath = @"\\printsrv01\Lobby-Mono",
            DisplayName = "Lobby Mono",
        };

        db.Printers.AddRange(hrMfp, engColor, lobbyMono);

        var hrZone = new ZoneEntity
        {
            Name = "HR",
            Priority = 50,
            Rules =
            [
                new ZoneRuleEntity { GroupSid = "S-1-5-21-DEMO-HR" },
            ],
            Printers = [hrMfp, lobbyMono],
        };
        var engZone = new ZoneEntity
        {
            Name = "Engineering",
            Priority = 50,
            Rules =
            [
                new ZoneRuleEntity { GroupSid = "S-1-5-21-DEMO-ENG" },
            ],
            Printers = [engColor, lobbyMono],
        };

        db.Zones.AddRange(hrZone, engZone);
        await db.SaveChangesAsync(ct);
    }
}
