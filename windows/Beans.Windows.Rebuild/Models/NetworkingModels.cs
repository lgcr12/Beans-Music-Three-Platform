using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Beans.Windows.Rebuild.Models;

public enum DiscoveryDataOrigin
{
    Live,
    CacheFresh,
    CacheStale,
    Preview
}

public enum PlatformCachePolicy
{
    NetworkOnly,
    CacheFirst,
    Refresh,
    StaleIfError,
    PreviewOnly
}

public enum PlatformErrorCode
{
    Cancelled,
    NetworkUnavailable,
    Timeout,
    Unauthorized,
    CredentialExpired,
    Forbidden,
    RateLimited,
    RegionRestricted,
    Unsupported,
    NotFound,
    InvalidRequest,
    InvalidResponse,
    ParseFailure,
    ServiceUnavailable,
    SecurityFailure,
    Unknown
}

public sealed record PlatformError(
    string PlatformId,
    PlatformErrorCode Code,
    string SafeMessage,
    bool IsTransient,
    bool CanRetry,
    bool RequiresLogin,
    string CorrelationId,
    HttpStatusCode? HttpStatusCode = null);

public sealed record PlatformResponse<T>(
    bool IsSuccess,
    T? Value,
    PlatformError? Error,
    HttpStatusCode? StatusCode,
    string CorrelationId,
    TimeSpan Elapsed,
    int RetryCount,
    DiscoveryDataOrigin DataOrigin,
    DateTimeOffset LoadedAt,
    bool IsStale,
    string? CacheKey,
    string SafeMessage)
{
    public static PlatformResponse<T> Success(
        T value,
        string correlationId,
        TimeSpan elapsed,
        int retryCount = 0,
        DiscoveryDataOrigin origin = DiscoveryDataOrigin.Live,
        HttpStatusCode? statusCode = HttpStatusCode.OK,
        string? cacheKey = null,
        bool isStale = false,
        string safeMessage = "内容已更新") =>
        new(true, value, null, statusCode, correlationId, elapsed, retryCount, origin, DateTimeOffset.UtcNow, isStale, cacheKey, safeMessage);

    public static PlatformResponse<T> Failure(
        PlatformError error,
        TimeSpan elapsed,
        int retryCount = 0,
        HttpStatusCode? statusCode = null,
        string? cacheKey = null) =>
        new(false, default, error, statusCode, error.CorrelationId, elapsed, retryCount, DiscoveryDataOrigin.Live, DateTimeOffset.UtcNow, false, cacheKey, error.SafeMessage);
}

public sealed class PlatformRequestContext
{
    public PlatformRequestContext(
        string platformId,
        string operationName,
        string requestKey,
        TimeSpan timeout,
        bool isAuthenticatedRequest,
        PlatformCachePolicy cachePolicy,
        string accountIdentityHash,
        bool allowRetry,
        int maximumRetries,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        if (!PlatformIdExtensions.TryParseStableId(platformId, out _)) throw new ArgumentException("Unknown platform ID", nameof(platformId));
        if (string.IsNullOrWhiteSpace(operationName)) throw new ArgumentException("Operation name is required", nameof(operationName));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumRetries is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maximumRetries));
        RequestKeyValidator.Validate(platformId, requestKey);
        if (string.IsNullOrWhiteSpace(accountIdentityHash)) throw new ArgumentException("Account identity hash is required", nameof(accountIdentityHash));

        PlatformId = platformId;
        OperationName = operationName;
        CorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
        RequestKey = requestKey;
        Timeout = timeout;
        IsAuthenticatedRequest = isAuthenticatedRequest;
        CachePolicy = cachePolicy;
        AccountIdentityHash = accountIdentityHash;
        AllowRetry = allowRetry && !isAuthenticatedRequest;
        MaximumRetries = AllowRetry ? maximumRetries : 0;
        CancellationToken = cancellationToken;
    }

    public string PlatformId { get; }
    public string OperationName { get; }
    public string CorrelationId { get; }
    public string RequestKey { get; }
    public TimeSpan Timeout { get; }
    public bool IsAuthenticatedRequest { get; }
    public PlatformCachePolicy CachePolicy { get; }
    public string AccountIdentityHash { get; }
    public bool AllowRetry { get; }
    public int MaximumRetries { get; }
    public CancellationToken CancellationToken { get; }

    public static PlatformRequestContext PublicDiscovery(
        string platformId,
        string operationName,
        string requestKey,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        new(platformId, operationName, requestKey, timeout ?? TimeSpan.FromSeconds(12), false,
            PlatformCachePolicy.StaleIfError, AccountIdentityHasher.Anonymous, true, 1, cancellationToken);
}

public static class AccountIdentityHasher
{
    public const string Anonymous = "anonymous";

    public static string Hash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Anonymous;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("beans-account-v1:" + value));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

public static class RequestKeyValidator
{
    private static readonly string[] SensitiveSegments =
    [
        "cookie", "token", "authorization", "password", "secret", "api-key", "api_key", "apikey", "key=",
        "signature", "sign=", "session", "csrf", "uin=", "?", "&"
    ];

    public static void Validate(string platformId, string requestKey)
    {
        if (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 256)
            throw new ArgumentException("Request key is missing or too long", nameof(requestKey));
        if (!requestKey.StartsWith(platformId + ":", StringComparison.Ordinal))
            throw new ArgumentException("Request key must begin with the platform ID", nameof(requestKey));
        if (SensitiveSegments.Any(segment => requestKey.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Request key contains a sensitive or unsafe segment", nameof(requestKey));
    }
}

public readonly record struct PlatformParseResult<T>(bool IsSuccess, T? Value, PlatformErrorCode? ErrorCode, string SafeMessage)
{
    public static PlatformParseResult<T> Success(T value) => new(true, value, null, "响应解析完成");
    public static PlatformParseResult<T> Failure(PlatformErrorCode code, string safeMessage) => new(false, default, code, safeMessage);
}
