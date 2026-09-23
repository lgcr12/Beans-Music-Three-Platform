using System.Text.Json;
using System.Text.Json.Serialization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Playback;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

/// <summary>
/// Performs the authenticated NetEase account request needed to validate a
/// filtered browser session. Session material never enters logs or request keys.
/// </summary>
public sealed class NetEaseAuthorizationProbe : INetEaseAuthorizationProbe
{
    private static readonly Uri Endpoint = new("https://music.163.com/api/nuser/account/get");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformResponseParser<NetEaseProbePayload> _parser;

    public NetEaseAuthorizationProbe(IPlatformHttpClientFactory factory, IPlatformJsonSerializer serializer)
    {
        _httpClient = factory.Get("netease");
        _parser = new JsonPlatformResponseParser<NetEaseProfileEnvelope, NetEaseProbePayload>(serializer, Parse);
    }

    public async Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials is null || !credentials.TryGetValue(NetEaseAuthCredentialNames.Session, out var rawSession) ||
            !NetEaseAuthorizationCookieExtractor.TryExtractSession(ProviderCredentialReader.ParseCookieHeader(rawSession), out var session))
            return CredentialState.NotAuthorized;

        var context = new PlatformRequestContext(
            "netease", "auth-probe", $"netease:auth-probe:{AccountIdentityHasher.Hash(session)}",
            TimeSpan.FromSeconds(12), true, PlatformCachePolicy.NetworkOnly,
            AccountIdentityHasher.Hash(session), false, 0, cancellationToken);
        var response = await _httpClient.SendAsync(
            () => CreateRequest(session), context, _parser);
        if (response.IsSuccess && response.Value?.IsValid == true)
            return CredentialState.Valid;
        return response.Error?.Code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired
            ? CredentialState.Expired
            : CredentialState.Error;
    }

    private static HttpRequestMessage CreateRequest(string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.UserAgent.ParseAdd("BeansMusic-Windows/Phase7");
        ProviderCredentialReader.ApplyCookieHeader(request, session);
        return request;
    }

    private static PlatformParseResult<NetEaseProbePayload> Parse(NetEaseProfileEnvelope envelope)
    {
        if (envelope.Code is not 200)
            return PlatformParseResult<NetEaseProbePayload>.Failure(
                envelope.Code is 301 or 401 or 403 ? PlatformErrorCode.Unauthorized : PlatformErrorCode.InvalidResponse,
                "网易云音乐账号验证未通过");
        if (envelope.Profile is null || envelope.Profile.Value.ValueKind != JsonValueKind.Object ||
            !envelope.Profile.Value.TryGetProperty("userId", out var userId) ||
            !userId.TryGetInt64(out var id) || id <= 0)
            return PlatformParseResult<NetEaseProbePayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐账号验证响应无效");
        return PlatformParseResult<NetEaseProbePayload>.Success(new NetEaseProbePayload(true));
    }

    private sealed record NetEaseProfileEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("profile")] JsonElement? Profile);

    private sealed record NetEaseProbePayload(bool IsValid);
}
