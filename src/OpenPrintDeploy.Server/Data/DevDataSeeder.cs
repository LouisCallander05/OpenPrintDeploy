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
        if (await db.Zones.AnyAsync(ct))
        {
            return;
        }

        var hrMfp = new PrinterEntity
        {
            UncPath = @"\\printsrv01\HR-MFP-01",
            DisplayName = "HR Multifunction (Floor 2)",
            Location = "Building A · Floor 2 · HR area",
        };
        var engColor = new PrinterEntity
        {
            UncPath = @"\\printsrv01\ENG-Color-01",
            DisplayName = "Engineering Colour",
            Location = "Building B · Floor 3",
        };
        var lobbyMono = new PrinterEntity
        {
            UncPath = @"\\printsrv01\Lobby-Mono",
            DisplayName = "Lobby Mono",
            Location = "Building A · Ground floor",
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
            DefaultPrinterId = hrMfp.Id,
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
            DefaultPrinterId = engColor.Id,
        };
        var lobbyZone = new ZoneEntity
        {
            Name = "Lobby network",
            Priority = 10,
            Rules =
            [
                new ZoneRuleEntity { SubnetCidr = "10.10.10.0/24" },
            ],
            Printers = [lobbyMono],
        };

        db.Zones.AddRange(hrZone, engZone, lobbyZone);
        await db.SaveChangesAsync(ct);
    }
}
