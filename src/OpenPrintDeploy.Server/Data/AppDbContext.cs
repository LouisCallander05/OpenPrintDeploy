using Microsoft.EntityFrameworkCore;
using OpenPrintDeploy.Server.Data.Entities;

namespace OpenPrintDeploy.Server.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<PrinterEntity> Printers => Set<PrinterEntity>();
    public DbSet<RemovedPrinterEntity> RemovedPrinters => Set<RemovedPrinterEntity>();
    public DbSet<ZoneEntity> Zones => Set<ZoneEntity>();
    public DbSet<ZoneRuleEntity> ZoneRules => Set<ZoneRuleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var printer = modelBuilder.Entity<PrinterEntity>();
        printer.HasKey(p => p.Id);
        printer.Property(p => p.UncPath).HasMaxLength(260).IsRequired();
        printer.Property(p => p.DisplayName).HasMaxLength(128).IsRequired();
        printer.HasIndex(p => p.UncPath).IsUnique();

        var removedPrinter = modelBuilder.Entity<RemovedPrinterEntity>();
        removedPrinter.HasKey(p => p.Id);
        removedPrinter.Property(p => p.UncPath).HasMaxLength(260).IsRequired();
        removedPrinter.HasIndex(p => p.UncPath).IsUnique();

        var zone = modelBuilder.Entity<ZoneEntity>();
        zone.HasKey(z => z.Id);
        zone.Property(z => z.Name).HasMaxLength(128).IsRequired();
        zone.HasIndex(z => z.Name).IsUnique();

        var rule = modelBuilder.Entity<ZoneRuleEntity>();
        rule.HasKey(r => r.Id);
        rule.Property(r => r.GroupSid).HasMaxLength(256);
        rule.HasOne(r => r.Zone)
            .WithMany(z => z.Rules)
            .HasForeignKey(r => r.ZoneId)
            .OnDelete(DeleteBehavior.Cascade);

        // ZonePrinter join table: implicit many-to-many via skip navigations.
        zone.HasMany(z => z.Printers)
            .WithMany(p => p.Zones)
            .UsingEntity(j => j.ToTable("ZonePrinters"));
    }
}
