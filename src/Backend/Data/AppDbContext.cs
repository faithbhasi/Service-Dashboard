using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Models;

namespace ServiceDashboard.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<GroupMapping> GroupMappings => Set<GroupMapping>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<LogoAsset> LogoAssets => Set<LogoAsset>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no time zone: always store and read UTC.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.Subject).IsUnique();
            e.HasIndex(x => x.Email);
            e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(256);
        });

        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RolePermission>(e => e.HasKey(x => new { x.RoleId, x.Permission }));

        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GroupMapping>(e =>
        {
            e.HasIndex(x => new { x.OktaGroup, x.RoleId }).IsUnique();
            e.Property(x => x.OktaGroup).HasMaxLength(256);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SettingEntry>(e => e.HasKey(x => x.Key));
        b.Entity<LogoAsset>(e => e.HasKey(x => x.Kind));

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.TimeUtc);
            e.HasIndex(x => new { x.Category, x.TimeUtc });
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.TargetId);
            e.HasIndex(x => x.Action);
        });
    }
}

public sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
    v => v == null ? null : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
    v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
