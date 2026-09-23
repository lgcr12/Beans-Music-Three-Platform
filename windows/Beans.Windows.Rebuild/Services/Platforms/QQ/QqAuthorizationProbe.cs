using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Playback;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

/// <summary>
/// Performs the minimum authenticated QQ profile request needed to validate a
/// filtered browser session. The probe never persists or logs cookie values.
/// </summary>
public sealed class QqAuthorizationProbe : IQqAuthorizationProbe
{
    private static readonly Uri Endpoint = new("https://c.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformResponseParser<QqProbePayload> _parser;

    public QqAuthorizationProbe(IPlatformHttpClientFactory factory, IPlatformJsonSerializer serializer)
    {
        _httpClient = factory.Get("qq");
        _parser = new JsonPlatformResponseParser<QqProfileEnvelope, QqProbePayload>(serializer, Parse);
    }

    public async Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials is null || !credentials.TryGetValue(QqAuthCredentialNames.Session, out var rawSession) ||
            !QqAuthorizationCookieExtractor.TryExtractSession(ProviderCredentialReader.ParseCookieHeader(rawSession), out var session))
            return CredentialState.NotAuthorized;

        var cookies = ProviderCredentialReader.ParseCookieHeader(session);
        var uin = First(cookies, "uin", "wxuin")?.TrimStart('o', 'O');
        if (!long.TryParse(uin, NumberStyles.None, CultureInfo.InvariantCulture, out var numericUin) || numericUin <= 0)
            return CredentialState.NotAuthorized;

        var requestKey = $"qq:auth-probe:{StableHash(uin!)}";
        var context = new PlatformRequestContext(
            "qq", "auth-probe", requestKey, TimeSpan.FromSeconds(12), true,
            PlatformCachePolicy.NetworkOnly, AccountIdentityHasher.Hash(uin!), false, 0, cancellationToken);
        var response = await _httpClient.SendAsync(
            () => CreateRequest(numericUin, uin!, session, cookies), context, _parser);
        if (response.IsSuccess && response.Value?.IsValid == true)
            return CredentialState.Valid;
        return response.Error?.Code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired
            ? CredentialState.Expired
            : CredentialState.Error;
    }

    private static HttpRequestMessage CreateRequest(long numericUin, string uin, string session, IReadOnlyDictionary<string, string> cookies)
    {
        var key = First(cookies, "p_skey", "skey") ?? string.Empty;
        var query = $"cid=205360838&userid={Uri.EscapeDataString(uin)}&reqfrom=1&g_tk={Gtk(key)}&loginUin={Uri.EscapeDataString(uin)}&format=json";
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Endpoint}?{query}"));
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.UserAgent.ParseAdd("BeansMusic-Windows/Phase7");
        ProviderCredentialReader.ApplyCookieHeader(request, session);
        return request;
    }

    private static PlatformParseResult<QqProbePayload> Parse(QqProfileEnvelope envelope)
    {
        if (envelope.Code is not null && envelope.Code != 0 || envelope.Result is not null && envelope.Result != 0)
            return PlatformParseResult<QqProbePayload>.Failure(PlatformErrorCode.Unauthorized, "QQ 音乐账号验证未通过");
        if (envelope.Data is null || envelope.Data.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return PlatformParseResult<QqProbePayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐账号验证响应无效");
        return PlatformParseResult<QqProbePayload>.Success(new QqProbePayload(true));
    }

    private static int Gtk(string value)
    {
        long hash = 5381;
        foreach (var character in value) hash += (hash << 5) + character;
        return (int)(hash & 0x7fffffff);
    }

    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] names) =>
        names.Select(name => values.TryGetValue(name, out var value) ? value : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private sealed record QqProfileEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("result")] int? Result,
        [property: JsonPropertyName("data")] JsonElement? Data);

    private sealed record QqProbePayload(bool IsValid);
}
