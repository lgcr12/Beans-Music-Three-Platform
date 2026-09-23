using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.Playback;

public sealed class NetEasePlaybackSourceResolver : IPlaybackSourceResolver
{
    private static readonly Uri Endpoint = new("https://music.163.com/api/song/enhance/player/url");
    private readonly IPlatformHttpClient _httpClient;
    private readonly ISecureCredentialStore _credentials;
    private readonly IPlatformResponseParser<NetEasePlaybackPayload> _parser;

    public NetEasePlaybackSourceResolver(
        IPlatformHttpClientFactory httpClientFactory,
        ISecureCredentialStore credentials,
        IPlatformJsonSerializer serializer)
    {
        _httpClient = httpClientFactory.Get("netease");
        _credentials = credentials;
        _parser = new JsonPlatformResponseParser<NetEasePlaybackEnvelope, NetEasePlaybackPayload>(serializer, Parse);
    }

    public PlatformId Platform => PlatformId.NetEaseMusic;

    public async Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Identity.Platform != Platform || !long.TryParse(request.Identity.NativeId, out var songId) || songId <= 0)
            return PlaybackSourceResult.Unavailable(PlaybackRestriction.ProviderUnavailable, "网易云音乐歌曲标识无效");

        var session = await ProviderCredentialReader.ReadSessionAsync(_credentials, "netease", cancellationToken);
        if (session is null)
            return PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "请先登录网易云音乐");

        foreach (var bitrate in QualityOrder(request.RequestedQuality))
        {
            var key = $"netease:playback:{StableKey(request.Identity.NativeId)}:{bitrate}";
            var context = new PlatformRequestContext(
                "netease", "playback", key, TimeSpan.FromSeconds(12), true,
                PlatformCachePolicy.NetworkOnly, AccountIdentityHasher.Hash(session), false, 0, cancellationToken);
            var response = await _httpClient.SendAsync(
                () => CreateRequest(songId, bitrate, session), context, _parser);
            if (response.IsSuccess && response.Value?.Source is { } source)
                return new PlaybackSourceResult(true, source, PlaybackRestriction.None, "网易云音乐播放源已准备");
            if (response.Error?.RequiresLogin == true || response.Error?.Code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired)
                return PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "网易云音乐登录已失效");
        }

        return PlaybackSourceResult.Unavailable(PlaybackRestriction.ProviderUnavailable, "网易云音乐未返回可播放地址");
    }

    private static HttpRequestMessage CreateRequest(long songId, int bitrate, string session)
    {
        var uri = new Uri($"{Endpoint}?id={songId.ToString(CultureInfo.InvariantCulture)}&ids=%5B{songId}%5D&br={bitrate.ToString(CultureInfo.InvariantCulture)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.UserAgent.ParseAdd("BeansMusic-Windows/Phase7");
        ProviderCredentialReader.ApplyCookieHeader(request, session);
        return request;
    }

    private static PlatformParseResult<NetEasePlaybackPayload> Parse(NetEasePlaybackEnvelope envelope)
    {
        if (envelope.Code is 301 or 401 or 403)
            return PlatformParseResult<NetEasePlaybackPayload>.Failure(PlatformErrorCode.Unauthorized, "网易云音乐需要登录");
        if (envelope.Data is null || envelope.Data.Count == 0)
            return PlatformParseResult<NetEasePlaybackPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐未返回播放信息");

        var item = envelope.Data.FirstOrDefault(value => Uri.TryCreate(value.Url, UriKind.Absolute, out _));
        return item?.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? PlatformParseResult<NetEasePlaybackPayload>.Success(new NetEasePlaybackPayload(new PlaybackSource(uri, Quality(item.Bitrate), null)))
            : PlatformParseResult<NetEasePlaybackPayload>.Failure(PlatformErrorCode.NotFound, "网易云音乐没有可播放地址");
    }

    private static AudioQuality Quality(int? bitrate) => bitrate switch
    {
        >= 900000 => AudioQuality.HiRes,
        >= 500000 => AudioQuality.Lossless,
        >= 300000 => AudioQuality.VeryHigh,
        >= 180000 => AudioQuality.High,
        _ => AudioQuality.Standard
    };

    private static IReadOnlyList<int> QualityOrder(AudioQuality quality) => quality switch
    {
        AudioQuality.HiRes or AudioQuality.Lossless => [999000, 320000, 192000, 128000],
        AudioQuality.VeryHigh => [320000, 192000, 128000],
        AudioQuality.High => [192000, 128000],
        _ => [128000]
    };

    private static string StableKey(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private sealed record NetEasePlaybackEnvelope(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("data")] IReadOnlyList<NetEasePlaybackItem>? Data);

    private sealed record NetEasePlaybackItem(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("br")] int? Bitrate);

    private sealed record NetEasePlaybackPayload(PlaybackSource Source);
}
