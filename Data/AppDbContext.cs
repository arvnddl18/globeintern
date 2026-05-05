using Microsoft.EntityFrameworkCore;
using SlotAd_Globe.Models;

namespace SlotAd_Globe.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ReportUploadEntity> ReportUploads => Set<ReportUploadEntity>();
    public DbSet<ReportDashboardArchiveEntity> ReportDashboardArchives => Set<ReportDashboardArchiveEntity>();
    public DbSet<ToolAuditSessionEntity> ToolAuditSessions => Set<ToolAuditSessionEntity>();
    public DbSet<ToolAuditEntryEntity> ToolAuditEntries => Set<ToolAuditEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => e.UserName).IsUnique();
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.PasswordSalt).HasMaxLength(88);
        });

        modelBuilder.Entity<ReportUploadEntity>(entity =>
        {
            entity.ToTable("ReportUploads");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.Token).IsUnique();
            entity.Property(e => e.OriginalFileName).HasMaxLength(512);
            entity.Property(e => e.CsvSourceKind).HasConversion<int>();
            entity.Property(e => e.CsvContent);
            entity.Property(e => e.SessionJson).IsRequired();

            entity.HasOne(e => e.User)
                .WithMany(u => u.ReportUploads)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.UploadedUtc });
            entity.HasIndex(e => new { e.CsvSourceKind, e.UploadedUtc });
        });

        modelBuilder.Entity<ReportDashboardArchiveEntity>(entity =>
        {
            entity.ToTable("ReportDashboardArchives");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Token).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.Token).IsUnique();
            entity.Property(e => e.OriginalFileName).HasMaxLength(512);
            entity.Property(e => e.CsvSourceKind).HasConversion<int>();
            entity.Property(e => e.SessionJson).IsRequired();
            entity.Property(e => e.PendingKpiJson).IsRequired();
            entity.Property(e => e.StatusKpiJson).IsRequired();
            entity.Property(e => e.PendingFilteredXlsxBytes);
            entity.Property(e => e.StatusFilteredXlsxBytes);
            entity.Property(e => e.LegacyGenerateXlsxBytes);

            entity.HasIndex(e => new { e.UserId, e.UploadedUtc });

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ToolAuditSessionEntity>(entity =>
        {
            entity.ToTable("ToolAuditSessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OriginalFileName).HasMaxLength(512);
            entity.Property(e => e.AuditDate);
            entity.HasIndex(e => e.WeekStartDate);
            entity.HasIndex(e => new { e.UploadedByUserId, e.UploadedUtc });

            entity.HasOne(e => e.UploadedByUser)
                .WithMany()
                .HasForeignKey(e => e.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ToolAuditEntryEntity>(entity =>
        {
            entity.ToTable("ToolAuditEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TechnicianName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ToolName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.RawValue).HasMaxLength(128);

            entity.HasOne(e => e.Session)
                .WithMany(s => s.Entries)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.SessionId, e.TechnicianName });
            entity.HasIndex(e => new { e.SessionId, e.ToolName, e.Status });
        });
    }
}
