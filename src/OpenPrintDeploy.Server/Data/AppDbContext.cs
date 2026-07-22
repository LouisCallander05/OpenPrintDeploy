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
    public DbSet<ClientDeviceEntity> ClientDevices => Set<ClientDeviceEntity>();
    public DbSet<ClientUserEntity> ClientUsers => Set<ClientUserEntity>();
    public DbSet<ClientPrinterEntity> ClientPrinters => Set<ClientPrinterEntity>();
    public DbSet<ClientActivityEntity> ClientActivities => Set<ClientActivityEntity>();

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

        var clientDevice = modelBuilder.Entity<ClientDeviceEntity>();
        clientDevice.HasKey(d => d.Id);
        clientDevice.Property(d => d.MachineName).HasMaxLength(128).IsRequired();
        clientDevice.Property(d => d.NormalizedMachineName).HasMaxLength(128).IsRequired();
        clientDevice.Property(d => d.ClientVersion).HasMaxLength(64);
        clientDevice.HasIndex(d => d.NormalizedMachineName).IsUnique();

        var clientUser = modelBuilder.Entity<ClientUserEntity>();
        clientUser.HasKey(u => u.Id);
        clientUser.Property(u => u.Username).HasMaxLength(256).IsRequired();
        clientUser.Property(u => u.NormalizedUsername).HasMaxLength(256).IsRequired();
        clientUser.Property(u => u.LastSyncStatus).HasMaxLength(32).IsRequired();
        clientUser.Property(u => u.LastError).HasMaxLength(1024);
        clientUser.HasIndex(u => new { u.DeviceId, u.NormalizedUsername }).IsUnique();
        clientUser.HasOne(u => u.Device)
            .WithMany(d => d.Users)
            .HasForeignKey(u => u.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        var clientPrinter = modelBuilder.Entity<ClientPrinterEntity>();
        clientPrinter.HasKey(p => p.Id);
        clientPrinter.Property(p => p.DisplayName).HasMaxLength(128);
        clientPrinter.Property(p => p.UncPath).HasMaxLength(260).IsRequired();
        clientPrinter.Property(p => p.NormalizedUncPath).HasMaxLength(260).IsRequired();
        clientPrinter.Property(p => p.Status).HasMaxLength(32).IsRequired();
        clientPrinter.Property(p => p.LastOperation).HasMaxLength(32);
        clientPrinter.Property(p => p.LastError).HasMaxLength(1024);
        clientPrinter.HasIndex(p => new { p.ClientUserId, p.NormalizedUncPath }).IsUnique();
        clientPrinter.HasOne(p => p.ClientUser)
            .WithMany(u => u.Printers)
            .HasForeignKey(p => p.ClientUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var clientActivity = modelBuilder.Entity<ClientActivityEntity>();
        clientActivity.HasKey(a => a.Id);
        clientActivity.Property(a => a.Type).HasMaxLength(32).IsRequired();
        clientActivity.Property(a => a.Summary).HasMaxLength(512).IsRequired();
        clientActivity.Property(a => a.PrinterDisplayName).HasMaxLength(128);
        clientActivity.Property(a => a.PrinterUncPath).HasMaxLength(260);
        clientActivity.Property(a => a.Error).HasMaxLength(1024);
        clientActivity.HasIndex(a => new { a.ClientUserId, a.OccurredAt });
        clientActivity.HasOne(a => a.ClientUser)
            .WithMany(u => u.Activities)
            .HasForeignKey(a => a.ClientUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
