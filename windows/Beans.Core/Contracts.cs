using System.Text.Json.Serialization;

namespace Beans.Core;

public sealed record CryptoProfile(
    string Algorithm,
    string Salt,
    int MemoryKiB,
    int Iterations,
    int Parallelism);

public sealed record DeviceInput(Guid Id, string Name, string Platform);
public sealed record AccountRecord(Guid Id, string Nickname, string Email, DateTimeOffset CreatedAt);
public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
public sealed record PendingRegistration(Guid RegistrationId, DateTimeOffset ExpiresAt, string? DevelopmentCode);
public sealed record PasswordResetStartResult(string? DevelopmentCode);
public sealed record AuthChallenge(CryptoProfile CryptoProfile);
public sealed record AuthSession(
    AccountRecord Account,
    string WrappedVaultKey,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    [JsonIgnore] public TokenPair Tokens => new(AccessToken, AccessTokenExpiresAt, RefreshToken, RefreshTokenExpiresAt);
}

public sealed record VaultEnvelope(long Version, string Ciphertext, DateTimeOffset UpdatedAt);
public sealed record SyncEnvelope(
    Guid Id,
    string EntityType,
    string EntityId,
    Guid DeviceId,
    long BaseRevision,
    long Revision,
    bool Deleted,
    string Ciphertext,
    DateTimeOffset UpdatedAt);
public sealed record SyncPage(long Cursor, bool HasMore, IReadOnlyList<SyncEnvelope> Records);
public sealed record QrSession(
    Guid Id,
    string Status,
    string VerificationCode,
    DateTimeOffset ExpiresAt,
    DeviceInput Device,
    string EphemeralPublicKey);
public sealed record QrExchangeResult(
    AccountRecord Account,
    string EncryptedVaultKey,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    [JsonIgnore] public TokenPair Tokens => new(AccessToken, AccessTokenExpiresAt, RefreshToken, RefreshTokenExpiresAt);
}

public sealed record DeviceRecord(Guid Id, string Name, string Platform, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Revoked);
public sealed record QrLoginContext(QrSession Session, byte[] ExchangeSecret, byte[] PrivateKey)
{
    [JsonIgnore] public string Payload => $"beans://account/qr?session={Session.Id}";
}

public sealed record ThemePayload(
    string ReferenceStyle,
    string Accent,
    string? CustomAccentHex,
    string BackgroundHex,
    bool BackgroundSyncAll,
    string UiStyle,
    int? FontScalePercent = null);

public sealed record PlatformPreferencePayload(
    IReadOnlyList<string> EnabledPlatforms,
    string? PlayerEffectMode = null,
    double? PlayerEffectIntensity = null,
    string? LyricStylePreset = null,
    double? LyricFontSize = null,
    double? LyricLineSpacing = null,
    bool? LyricTranslation = null,
    string? LyricAlignment = null);
public sealed record PlatformMirrorPayload(string Platform, IReadOnlyList<MirrorPlaylist> Playlists, DateTimeOffset UpdatedAt);
public sealed record MirrorPlaylist(long Id, string Name, Uri? CoverUrl, int TrackCount, string CreatorName, string Source);

public sealed record PlatformCredentialBundle(
    Dictionary<string, string>? Qq,
    Dictionary<string, string>? Netease,
    DateTimeOffset UpdatedAt);

public static class PlatformCredentialPolicy
{
    public static bool LooksUsable(string provider, IReadOnlyDictionary<string, string> cookies)
    {
        bool Has(string key) => cookies.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
        return provider switch
        {
            "netease" => Has("MUSIC_U"),
            "qq" => (Has("uin") || Has("wxuin")) && (Has("qm_keyst") || Has("qqmusic_key") || Has("p_skey") || Has("skey")),
            _ => false
        };
    }
}
