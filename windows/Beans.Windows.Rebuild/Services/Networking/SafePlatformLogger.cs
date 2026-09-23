using System.Text.RegularExpressions;
using Beans.Windows.Rebuild.Models;
using Microsoft.Extensions.Logging;

namespace Beans.Windows.Rebuild.Services.Networking;

public sealed record SafePlatformLogEvent(
    string PlatformId,
    string OperationName,
    string CorrelationId,
    string Host,
    string Path,
    PlatformErrorCode? ErrorCode,
    int? HttpStatusCode,
    long ElapsedMilliseconds,
    int RetryCount,
    DiscoveryDataOrigin DataOrigin);

public interface ISafeLogger
{
    void RequestStarted(SafePlatformLogEvent entry);
    void RequestCompleted(SafePlatformLogEvent entry);
}

public interface ISensitiveDataRedactor
{
    string Redact(string value);
    (string Host, string Path) SafeUriParts(Uri? uri);
}

public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    [GeneratedRegex("(?i)(cookie|set-cookie|token|authorization|password|passwd|secret|api[_-]?key|signature|session|csrf|uin|sign)\\s*[:=]\\s*([^&\\s;,]+)")]
    private static partial Regex SensitivePairPattern();

    [GeneratedRegex("(?i)bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var withoutBearer = BearerPattern().Replace(value, "Bearer [REDACTED]");
        return SensitivePairPattern().Replace(withoutBearer, match => $"{match.Groups[1].Value}=[REDACTED]");
    }

    public (string Host, string Path) SafeUriParts(Uri? uri) => uri is null
        ? (string.Empty, string.Empty)
        : (uri.Host, uri.AbsolutePath);
}

public sealed class SafePlatformLogger(ILogger<SafePlatformLogger> logger, ISensitiveDataRedactor redactor) : ISafeLogger
{
    public void RequestStarted(SafePlatformLogEvent entry)
    {
        var safe = Sanitize(entry);
        logger.LogInformation(
            "Platform request started Platform={PlatformId} Operation={OperationName} Correlation={CorrelationId} Host={Host} Path={Path} Retry={RetryCount}",
            safe.PlatformId, safe.OperationName, safe.CorrelationId, safe.Host, safe.Path, safe.RetryCount);
    }

    public void RequestCompleted(SafePlatformLogEvent entry)
    {
        var safe = Sanitize(entry);
        logger.LogInformation(
            "Platform request completed Platform={PlatformId} Operation={OperationName} Correlation={CorrelationId} Status={StatusCode} Error={ErrorCode} ElapsedMs={ElapsedMilliseconds} Retry={RetryCount} Origin={DataOrigin}",
            safe.PlatformId, safe.OperationName, safe.CorrelationId, safe.HttpStatusCode, safe.ErrorCode,
            safe.ElapsedMilliseconds, safe.RetryCount, safe.DataOrigin);
    }

    private SafePlatformLogEvent Sanitize(SafePlatformLogEvent entry) => entry with
    {
        OperationName = redactor.Redact(entry.OperationName),
        Host = redactor.Redact(entry.Host),
        Path = redactor.Redact(entry.Path)
    };
}
