using System.Net;
using System.Net.Sockets;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Networking;

public interface IPlatformErrorMapper
{
    PlatformError FromStatus(string platformId, string correlationId, HttpStatusCode statusCode);
    PlatformError FromException(string platformId, string correlationId, Exception exception, bool timedOut, bool callerCancelled);
    PlatformError FromCode(string platformId, string correlationId, PlatformErrorCode code, string? safeMessage = null);
}

public sealed class PlatformErrorMapper : IPlatformErrorMapper
{
    public PlatformError FromStatus(string platformId, string correlationId, HttpStatusCode statusCode)
    {
        var code = statusCode switch
        {
            HttpStatusCode.BadRequest => PlatformErrorCode.InvalidRequest,
            HttpStatusCode.Unauthorized => PlatformErrorCode.Unauthorized,
            HttpStatusCode.Forbidden => PlatformErrorCode.Forbidden,
            HttpStatusCode.NotFound => PlatformErrorCode.NotFound,
            (HttpStatusCode)429 => PlatformErrorCode.RateLimited,
            >= HttpStatusCode.InternalServerError => PlatformErrorCode.ServiceUnavailable,
            _ => PlatformErrorCode.Unknown
        };
        return Create(platformId, correlationId, code, (int)statusCode);
    }

    public PlatformError FromException(string platformId, string correlationId, Exception exception, bool timedOut, bool callerCancelled)
    {
        if (callerCancelled) return Create(platformId, correlationId, PlatformErrorCode.Cancelled);
        if (timedOut) return Create(platformId, correlationId, PlatformErrorCode.Timeout);
        if (exception is HttpRequestException { InnerException: SocketException } or SocketException)
            return Create(platformId, correlationId, PlatformErrorCode.NetworkUnavailable);
        if (exception is HttpRequestException) return Create(platformId, correlationId, PlatformErrorCode.NetworkUnavailable);
        return Create(platformId, correlationId, PlatformErrorCode.Unknown);
    }

    public PlatformError FromCode(string platformId, string correlationId, PlatformErrorCode code, string? safeMessage = null)
    {
        var error = Create(platformId, correlationId, code);
        return safeMessage is null ? error : error with { SafeMessage = safeMessage };
    }

    private static PlatformError Create(string platformId, string correlationId, PlatformErrorCode code, int? statusCode = null)
    {
        var (message, transient, retry, login) = code switch
        {
            PlatformErrorCode.Cancelled => ("请求已取消", false, false, false),
            PlatformErrorCode.NetworkUnavailable => ("网络暂时不可用", true, true, false),
            PlatformErrorCode.Timeout => ("平台响应超时", true, true, false),
            PlatformErrorCode.Unauthorized => ("需要登录后继续", false, false, true),
            PlatformErrorCode.CredentialExpired => ("登录已失效", false, false, true),
            PlatformErrorCode.Forbidden => ("当前请求无权访问", false, false, false),
            PlatformErrorCode.RateLimited => ("请求过于频繁，请稍后重试", true, true, false),
            PlatformErrorCode.RegionRestricted => ("当前地区不可用", false, false, false),
            PlatformErrorCode.Unsupported => ("当前平台尚未支持此能力", false, false, false),
            PlatformErrorCode.NotFound => ("未找到请求的内容", false, false, false),
            PlatformErrorCode.InvalidRequest => ("请求参数无效", false, false, false),
            PlatformErrorCode.InvalidResponse => ("平台返回的数据不完整", false, false, false),
            PlatformErrorCode.ParseFailure => ("平台响应无法解析", false, false, false),
            PlatformErrorCode.ServiceUnavailable => ("平台服务暂时不可用", true, true, false),
            PlatformErrorCode.SecurityFailure => ("请求未通过安全检查", false, false, false),
            _ => ("平台请求未能完成", false, false, false)
        };
        return new PlatformError(platformId, code, message, transient, retry, login, correlationId,
            statusCode is null ? null : (HttpStatusCode)statusCode.Value);
    }
}
