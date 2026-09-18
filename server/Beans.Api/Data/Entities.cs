namespace Beans.Api.Data;

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string Nickname { get; set; }
    public required string AuthSecretHash { get; set; }
    public required string CryptoProfileJson { get; set; }
    public required string WrappedVaultKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PendingRegistrationEntity
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string Nickname { get; set; }
    public required string AuthSecretHash { get; set; }
    public required string CryptoProfileJson { get; set; }
    public required string WrappedVaultKey { get; set; }
    public Guid DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public required string DevicePlatform { get; set; }
    public required string VerificationCodeHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
}

public sealed class VerificationCodeEntity
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string Purpose { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Platform { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public sealed class VaultEntity
{
    public Guid UserId { get; set; }
    public long Version { get; set; }
    public required string Ciphertext { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AccountCursorEntity
{
    public Guid UserId { get; set; }
    public long CurrentRevision { get; set; }
}

public sealed class SyncRecordEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public Guid DeviceId { get; set; }
    public long BaseRevision { get; set; }
    public long Revision { get; set; }
    public bool Deleted { get; set; }
    public required string Ciphertext { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class QrSessionEntity
{
    public Guid Id { get; set; }
    public Guid TargetDeviceId { get; set; }
    public required string TargetDeviceName { get; set; }
    public required string TargetPlatform { get; set; }
    public required string EphemeralPublicKey { get; set; }
    public required string ExchangeSecretHash { get; set; }
    public required string VerificationCode { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? EncryptedVaultKey { get; set; }
    public DateTimeOffset? ExchangedAt { get; set; }
}
