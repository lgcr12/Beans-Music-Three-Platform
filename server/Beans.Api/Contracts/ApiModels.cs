using System.ComponentModel.DataAnnotations;

namespace Beans.Api.Contracts;

public sealed record CryptoProfile(
    string Algorithm,
    string Salt,
    int MemoryKiB = 65536,
    int Iterations = 3,
    int Parallelism = 2);

public sealed record DeviceInput(Guid Id, string Name, string Platform);
public sealed record RegisterStartRequest(string Nickname, string Email, string AuthSecret, CryptoProfile CryptoProfile, string WrappedVaultKey, DeviceInput Device);
public sealed record PendingRegistration(Guid RegistrationId, DateTimeOffset ExpiresAt, string? DevelopmentCode = null);
public sealed record VerifyEmailRequest(Guid RegistrationId, [RegularExpression("^[0-9]{6}$")] string Code);
public sealed record LoginChallengeRequest(string Email);
public sealed record AuthChallenge(CryptoProfile CryptoProfile);
public sealed record LoginRequest(string Email, string AuthSecret, DeviceInput Device);
public sealed record RefreshRequest(string RefreshToken);
public sealed record AccountDto(Guid Id, string Nickname, string Email, DateTimeOffset CreatedAt);
public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
public sealed record AuthSession(AccountDto Account, string WrappedVaultKey, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
public sealed record PasswordResetStartRequest(string Email);
public sealed record PasswordResetRequest(string Email, string Code, string AuthSecret, CryptoProfile CryptoProfile, string WrappedVaultKey);
public sealed record DeviceRecord(Guid Id, string Name, string Platform, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Revoked);

public sealed record CreateQrSessionRequest(DeviceInput Device, string EphemeralPublicKey, string ExchangeSecret);
public sealed record QrSessionDto(Guid Id, string Status, string VerificationCode, DateTimeOffset ExpiresAt, DeviceInput Device, string EphemeralPublicKey);
public sealed record ApproveQrSessionRequest(string VerificationCode, string EncryptedVaultKey);
public sealed record ExchangeQrSessionRequest(DeviceInput Device, string EphemeralPublicKey, string ExchangeSecret);
public sealed record QrExchangeResult(AccountDto Account, string EncryptedVaultKey, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public sealed record VaultEnvelope(long Version, string Ciphertext, DateTimeOffset UpdatedAt);
public sealed record SyncEnvelope(Guid Id, string EntityType, string EntityId, Guid DeviceId, long BaseRevision, long Revision, bool Deleted, string Ciphertext, DateTimeOffset UpdatedAt);
public sealed record SyncPage(long Cursor, bool HasMore, IReadOnlyList<SyncEnvelope> Records);

public static class ContractValidation
{
    public static bool IsPlatform(string value) => value is "ios" or "macos" or "windows";
    public static bool IsEntityType(string value) => value is "playlist" or "playlistItem" or "favorite" or "history" or "playback" or "theme" or "preference" or "platformMirror";
    public static bool IsBase64Bytes(string value, int minimumBytes = 16)
    {
        try { return Convert.FromBase64String(value).Length >= minimumBytes; }
        catch (FormatException) { return false; }
    }
}
