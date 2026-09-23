namespace Beans.Windows.Rebuild.Services.BeansAccount;

public sealed record BeansCryptoProfile(
    string Algorithm,
    string Salt,
    int MemoryKiB = 65_536,
    int Iterations = 3,
    int Parallelism = 2);

internal sealed record BeansDeviceInput(Guid Id, string Name, string Platform);
internal sealed record BeansPendingRegistration(Guid RegistrationId, DateTimeOffset ExpiresAt, string? DevelopmentCode);
internal sealed record BeansAuthChallenge(BeansCryptoProfile CryptoProfile);
internal sealed record BeansAccountRecord(Guid Id, string Nickname, string Email, DateTimeOffset CreatedAt);
internal sealed record BeansTokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

internal sealed record BeansAuthSession(
    BeansAccountRecord Account,
    string WrappedVaultKey,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public BeansTokenPair Tokens => new(
        AccessToken,
        AccessTokenExpiresAt,
        RefreshToken,
        RefreshTokenExpiresAt);
}

public sealed record BeansSyncEnvelope(
    Guid Id,
    string EntityType,
    string EntityId,
    Guid DeviceId,
    long BaseRevision,
    long Revision,
    bool Deleted,
    string Ciphertext,
    DateTimeOffset UpdatedAt);

internal sealed record BeansSyncPage(long Cursor, bool HasMore, IReadOnlyList<BeansSyncEnvelope> Records);
internal sealed record BeansVaultEnvelope(long Version, string Ciphertext, DateTimeOffset UpdatedAt);
