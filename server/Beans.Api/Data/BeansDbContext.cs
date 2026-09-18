using Microsoft.EntityFrameworkCore;

namespace Beans.Api.Data;

public sealed class BeansDbContext(DbContextOptions<BeansDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<PendingRegistrationEntity> PendingRegistrations => Set<PendingRegistrationEntity>();
    public DbSet<VerificationCodeEntity> VerificationCodes => Set<VerificationCodeEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<VaultEntity> Vaults => Set<VaultEntity>();
    public DbSet<AccountCursorEntity> AccountCursors => Set<AccountCursorEntity>();
    public DbSet<SyncRecordEntity> SyncRecords => Set<SyncRecordEntity>();
    public DbSet<QrSessionEntity> QrSessions => Set<QrSessionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>().HasIndex(x => x.NormalizedEmail).IsUnique();
        modelBuilder.Entity<DeviceEntity>().HasKey(x => new { x.UserId, x.Id });
        modelBuilder.Entity<RefreshTokenEntity>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<VaultEntity>().HasKey(x => x.UserId);
        modelBuilder.Entity<AccountCursorEntity>().HasKey(x => x.UserId);
        modelBuilder.Entity<SyncRecordEntity>().HasIndex(x => new { x.UserId, x.Revision }).IsUnique();
        modelBuilder.Entity<SyncRecordEntity>().HasIndex(x => new { x.UserId, x.EntityType, x.EntityId });
        modelBuilder.Entity<QrSessionEntity>().HasIndex(x => x.ExpiresAt);
        modelBuilder.Entity<VerificationCodeEntity>().HasIndex(x => new { x.NormalizedEmail, x.Purpose });
    }
}
