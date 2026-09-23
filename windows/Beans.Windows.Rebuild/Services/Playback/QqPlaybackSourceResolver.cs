using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.Playback;

public sealed class QqPlaybackSourceResolver : IPlaybackSourceResolver
{
    private static readonly Uri Endpoint = new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private readonly IPlatformHttpClient _httpClient;
    private readonly ISecureCredentialStore _credentials;
    private readonly IPlatformJsonSerializer _serializer;

    public QqPlaybackSourceResolver(
        IPlatformHttpClientFactory httpClientFactory,
        ISecureCredentialStore credentials,
        IPlatformJsonSerializer serializer)
    {
        _httpClient = httpClientFactory.Get("qq");
        _credentials = credentials;
        _serializer = serializer;
    }

    public PlatformId Platform => PlatformId.QqMusic;

    public async Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var songMid = request.Identity.NativeId?.Trim();
        if (request.Identity.Platform != Platform || !IsSafeMid(songMid))
            return PlaybackSourceResult.Unavailable(PlaybackRestriction.FileUnavailable, "QQ 音乐歌曲标识无效");

        var session = await ProviderCredentialReader.ReadSessionAsync(_credentials, "qq", cancellationToken);
        if (session is null)
            return PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "请先登录 QQ 音乐");

        var cookies = ProviderCredentialReader.ParseCookieHeader(session);
        var uin = First(cookies, "uin", "wxuin")?.TrimStart('o', 'O');
        var loginKey = First(cookies, "qm_keyst", "qqmusic_key", "p_skey", "skey");
        if (!long.TryParse(uin, NumberStyles.None, CultureInfo.InvariantCulture, out var numericUin) ||
            numericUin <= 0 || string.IsNullOrWhiteSpace(loginKey))
            return PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "QQ 音乐登录已失效");

        var mediaMid = request.ProviderMediaId?.Trim();
        if (!IsSafeMid(mediaMid))
            mediaMid = await ResolveMediaMidAsync(songMid!, session, uin!, cancellationToken);
        if (!IsSafeMid(mediaMid)) mediaMid = songMid;

        var gtk = Gtk(First(cookies, "p_skey", "skey") ?? string.Empty);
        PlaybackSourceResult? bestFailure = null;
        foreach (var quality in QualityOrder(request.RequestedQuality))
        {
            var key = $"qq:playback:{StableKey(songMid!)}:{quality}";
            var context = AuthenticatedContext("playback", key, uin!, cancellationToken);
            var parser = new JsonPlatformResponseParser<QqPlaybackEnvelope, QqPlaybackAttempt>(
                _serializer, envelope => ParsePlayback(envelope, quality));
            var response = await _httpClient.SendAsync(
                () => CreateVkeyRequest(songMid!, mediaMid!, numericUin, uin!, loginKey!, gtk, quality, session),
                context, parser);

            if (response.IsSuccess && response.Value is { Candidates.Count: > 0 } playableAttempt)
            {
                var probed = await ProbeOfficialSourcesAsync(playableAttempt.Candidates, session, uin!, cancellationToken);
                if (probed.IsSuccess) return probed;
                bestFailure = Prefer(bestFailure, probed);
                continue;
            }

            if (response.Error?.RequiresLogin == true ||
                response.Error?.Code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired)
                return PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "QQ 音乐登录已失效");

            var failure = response.Value is { } attempt
                ? PlaybackSourceResult.Unavailable(attempt.Restriction, attempt.SafeMessage)
                : MapTransportFailure(response.Error?.Code);
            bestFailure = Prefer(bestFailure, failure);
        }

        return bestFailure ?? PlaybackSourceResult.Unavailable(
            PlaybackRestriction.FileUnavailable, "QQ 音乐未返回可播放地址");
    }

    private async Task<string?> ResolveMediaMidAsync(
        string songMid,
        string session,
        string uin,
        CancellationToken cancellationToken)
    {
        var parser = new JsonPlatformResponseParser<QqSongDetailEnvelope, string>(
            _serializer, envelope =>
            {
                var mediaMid = envelope.Request?.Data?.TrackInfo?.File?.MediaMid;
                return IsSafeMid(mediaMid)
                    ? PlatformParseResult<string>.Success(mediaMid!.Trim())
                    : PlatformParseResult<string>.Failure(PlatformErrorCode.NotFound, "QQ 音乐未返回媒体标识");
            });
        var context = AuthenticatedContext(
            "song-media", $"qq:song-media:{StableKey(songMid)}", uin, cancellationToken);
        var response = await _httpClient.SendAsync(
            () => CreateSongDetailRequest(songMid, session), context, parser);
        return response.IsSuccess ? response.Value : null;
    }

    private static HttpRequestMessage CreateSongDetailRequest(string songMid, string session)
    {
        var payload = JsonSerializer.Serialize(new
        {
            comm = new { ct = 24, cv = 0 },
            req_0 = new
            {
                module = "music.pf_song_detail_svr",
                method = "get_song_detail_yqq",
                param = new { song_mid = songMid }
            }
        });
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        AddOfficialHeaders(request, session);
        return request;
    }

    private static HttpRequestMessage CreateVkeyRequest(
        string songMid,
        string mediaMid,
        long numericUin,
        string uin,
        string loginKey,
        int gtk,
        string quality,
        string session)
    {
        var filename = $"{quality}{mediaMid}.{Extension(quality)}";
        var guidBytes = SHA256.HashData(Encoding.UTF8.GetBytes("beans-qq-guid:" + uin));
        var guid = (BitConverter.ToUInt32(guidBytes, 0) % 900_000_000 + 100_000_000)
            .ToString(CultureInfo.InvariantCulture);
        var payload = JsonSerializer.Serialize(new
        {
            comm = new { uin = numericUin, format = "json", ct = 19, cv = 0, g_tk = gtk, authst = loginKey },
            req = new
            {
                module = "CDN.SrfCdnDispatchServer",
                method = "GetCdnDispatch",
                param = new { guid, calltype = 0, userip = "" }
            },
            req_0 = new
            {
                module = "vkey.GetVkeyServer",
                method = "CgiGetVkey",
                param = new
                {
                    filename = new[] { filename },
                    guid,
                    songmid = new[] { songMid },
                    songtype = new[] { 0 },
                    uin,
                    loginflag = 1,
                    platform = "20"
                }
            }
        });
        var uri = new Uri($"{Endpoint}?format=json&g_tk={gtk.ToString(CultureInfo.InvariantCulture)}&data={Uri.EscapeDataString(payload)}");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddOfficialHeaders(request, session);
        return request;
    }

    private static void AddOfficialHeaders(HttpRequestMessage request, string session)
    {
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BeansMusic/1.0");
        ProviderCredentialReader.ApplyCookieHeader(request, session);
    }

    private async Task<PlaybackSourceResult> ProbeOfficialSourcesAsync(
        IReadOnlyList<PlaybackSource> candidates,
        string session,
        string uin,
        CancellationToken cancellationToken)
    {
        PlaybackSourceResult? bestFailure = null;
        foreach (var candidate in candidates)
        {
            if (!IsOfficialPlaybackUri(candidate.Uri)) continue;
            var context = AuthenticatedContext(
                "playback-probe",
                $"qq:playback-probe:{StableKey(candidate.Uri.AbsoluteUri)}",
                uin,
                cancellationToken);
            var response = await _httpClient.SendHeadersAsync(
                () => CreatePlaybackProbeRequest(candidate.Uri, session), context);
            if (response.IsSuccess && response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                return new PlaybackSourceResult(true, candidate, PlaybackRestriction.None, "QQ 音乐播放源已准备");

            bestFailure = Prefer(bestFailure, MapProbeFailure(response));
        }

        return bestFailure ?? PlaybackSourceResult.Unavailable(
            PlaybackRestriction.ProviderUnavailable, "QQ 音乐官方播放节点暂时不可用");
    }

    private static HttpRequestMessage CreatePlaybackProbeRequest(Uri uri, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        AddOfficialHeaders(request, session);
        return request;
    }

    private static PlatformParseResult<QqPlaybackAttempt> ParsePlayback(QqPlaybackEnvelope envelope, string quality)
    {
        if (envelope.Request?.Code is int requestCode && requestCode != 0)
            return PlatformParseResult<QqPlaybackAttempt>.Failure(
                requestCode is 401 or 1000 or 2000 ? PlatformErrorCode.Unauthorized : PlatformErrorCode.ServiceUnavailable,
                requestCode is 401 or 1000 or 2000 ? "QQ 音乐登录已失效" : "QQ 音乐播放服务暂时不可用");

        var data = envelope.Request?.Data;
        if (data?.MidUrlInfo is not { Count: > 0 } infos)
            return PlatformParseResult<QqPlaybackAttempt>.Failure(
                PlatformErrorCode.InvalidResponse, "QQ 音乐未返回播放信息");

        var info = infos.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Purl));
        if (info?.Purl is { } purl)
        {
            var sources = CreateOfficialSources(purl, data.Sip, quality);
            return sources.Count == 0
                ? PlatformParseResult<QqPlaybackAttempt>.Failure(
                    PlatformErrorCode.SecurityFailure, "QQ 音乐返回了无效播放地址")
                : PlatformParseResult<QqPlaybackAttempt>.Success(
                    new QqPlaybackAttempt(sources, PlaybackRestriction.None, "QQ 音乐播放源已准备"));
        }

        var first = infos[0];
        var restriction = ClassifyRestriction(first.Result, First(first.Message, data.Message));
        var message = restriction switch
        {
            PlaybackRestriction.RequiresAuthorization => "QQ 音乐登录已失效",
            PlaybackRestriction.SubscriptionRequired => "当前歌曲需要有效的 QQ 音乐会员权益",
            PlaybackRestriction.RegionRestricted => "当前歌曲在所在地区不可播放",
            _ => "QQ 音乐当前没有可用的官方播放源"
        };
        return PlatformParseResult<QqPlaybackAttempt>.Success(new QqPlaybackAttempt([], restriction, message));
    }

    private static IReadOnlyList<PlaybackSource> CreateOfficialSources(
        string purl,
        IReadOnlyList<string>? sip,
        string quality)
    {
        var bases = (sip ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        bases.AddRange([
            "https://isure.stream.qqmusic.qq.com/",
            "https://dl.stream.qqmusic.qq.com/",
            "https://ws.stream.qqmusic.qq.com/",
            "https://streamoc.music.tc.qq.com/"
        ]);

        var sources = new List<PlaybackSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseUrl in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? purl
                : baseUrl.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/" + purl.TrimStart('/');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsOfficialPlaybackUri(uri)) continue;
            if (seen.Add(uri.AbsoluteUri)) sources.Add(new PlaybackSource(uri, Quality(quality), null));
            if (purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) break;
        }
        return sources;
    }

    private static bool IsOfficialPlaybackUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.Port is -1 or 443 &&
        (uri.DnsSafeHost.EndsWith(".qqmusic.qq.com", StringComparison.OrdinalIgnoreCase) ||
         uri.DnsSafeHost.Equals("qqmusic.qq.com", StringComparison.OrdinalIgnoreCase) ||
         uri.DnsSafeHost.EndsWith(".music.tc.qq.com", StringComparison.OrdinalIgnoreCase) ||
         uri.DnsSafeHost.Equals("music.tc.qq.com", StringComparison.OrdinalIgnoreCase));

    private static PlaybackRestriction ClassifyRestriction(int? result, string? rawMessage)
    {
        var message = rawMessage ?? string.Empty;
        if (result is 401 or 1000 or 2000 || ContainsAny(message, "login", "登录", "auth", "cookie"))
            return PlaybackRestriction.RequiresAuthorization;
        if (ContainsAny(message, "region", "地区", "区域", "海外", "版权区域"))
            return PlaybackRestriction.RegionRestricted;
        if (result is 104003 or 104004 or 104005 or 105004 ||
            ContainsAny(message, "vip", "会员", "pay", "付费", "购买"))
            return PlaybackRestriction.SubscriptionRequired;
        return PlaybackRestriction.FileUnavailable;
    }

    private static PlaybackSourceResult MapTransportFailure(PlatformErrorCode? code) => code switch
    {
        PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired =>
            PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "QQ 音乐登录已失效"),
        PlatformErrorCode.RegionRestricted =>
            PlaybackSourceResult.Unavailable(PlaybackRestriction.RegionRestricted, "当前歌曲在所在地区不可播放"),
        PlatformErrorCode.Forbidden =>
            PlaybackSourceResult.Unavailable(PlaybackRestriction.SubscriptionRequired, "QQ 音乐未授予当前歌曲的播放权益"),
        PlatformErrorCode.NotFound =>
            PlaybackSourceResult.Unavailable(PlaybackRestriction.FileUnavailable, "QQ 音乐当前没有可用的官方播放源"),
        _ => PlaybackSourceResult.Unavailable(PlaybackRestriction.ProviderUnavailable, "QQ 音乐播放服务暂时不可用")
    };

    private static PlaybackSourceResult MapProbeFailure(PlatformResponse<HttpStatusCode> response)
    {
        if (response.StatusCode == HttpStatusCode.UnavailableForLegalReasons)
            return PlaybackSourceResult.Unavailable(
                PlaybackRestriction.RegionRestricted, "当前歌曲在所在地区不可播放");
        return MapTransportFailure(response.Error?.Code);
    }

    private static PlaybackSourceResult Prefer(PlaybackSourceResult? current, PlaybackSourceResult candidate)
    {
        static int Rank(PlaybackRestriction value) => value switch
        {
            PlaybackRestriction.RequiresAuthorization => 5,
            PlaybackRestriction.RegionRestricted => 4,
            PlaybackRestriction.SubscriptionRequired => 3,
            PlaybackRestriction.FileUnavailable => 2,
            _ => 1
        };
        return current is null || Rank(candidate.Restriction) > Rank(current.Restriction) ? candidate : current;
    }

    private static AudioQuality Quality(string quality) => quality switch
    {
        "RS01" => AudioQuality.HiRes,
        "F000" => AudioQuality.Lossless,
        "C400" => AudioQuality.VeryHigh,
        "M800" => AudioQuality.High,
        _ => AudioQuality.Standard
    };

    private static IReadOnlyList<string> QualityOrder(AudioQuality quality) => quality switch
    {
        AudioQuality.HiRes => ["RS01", "F000", "C400", "M800", "M500"],
        AudioQuality.Lossless => ["F000", "C400", "M800", "M500"],
        AudioQuality.VeryHigh => ["C400", "M800", "M500"],
        AudioQuality.High => ["M800", "M500"],
        _ => ["M500"]
    };

    private static string Extension(string quality) => quality switch
    {
        "RS01" or "F000" => "flac",
        "C400" => "m4a",
        _ => "mp3"
    };

    private static PlatformRequestContext AuthenticatedContext(
        string operation,
        string key,
        string uin,
        CancellationToken cancellationToken) =>
        new("qq", operation, key, TimeSpan.FromSeconds(12), true,
            PlatformCachePolicy.NetworkOnly, AccountIdentityHasher.Hash(uin), false, 0, cancellationToken);

    private static bool IsSafeMid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static int Gtk(string value)
    {
        long hash = 5381;
        foreach (var character in value) hash += (hash << 5) + character;
        return (int)(hash & 0x7fffffff);
    }

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] names) =>
        names.Select(name => values.TryGetValue(name, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private sealed record QqPlaybackEnvelope([property: JsonPropertyName("req_0")] QqRequest? Request);
    private sealed record QqRequest(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("data")] QqPlaybackData? Data);
    private sealed record QqPlaybackData(
        [property: JsonPropertyName("midurlinfo")] IReadOnlyList<QqPlaybackInfo>? MidUrlInfo,
        [property: JsonPropertyName("sip")] IReadOnlyList<string>? Sip,
        [property: JsonPropertyName("msg")] string? Message);
    private sealed record QqPlaybackInfo(
        [property: JsonPropertyName("purl")] string? Purl,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("type")] string? FileType,
        [property: JsonPropertyName("result")] int? Result,
        [property: JsonPropertyName("msg")] string? Message);
    private sealed record QqPlaybackAttempt(
        IReadOnlyList<PlaybackSource> Candidates,
        PlaybackRestriction Restriction,
        string SafeMessage);

    private sealed record QqSongDetailEnvelope([property: JsonPropertyName("req_0")] QqSongDetailRequest? Request);
    private sealed record QqSongDetailRequest([property: JsonPropertyName("data")] QqSongDetailData? Data);
    private sealed record QqSongDetailData([property: JsonPropertyName("track_info")] QqSongDetailTrack? TrackInfo);
    private sealed record QqSongDetailTrack([property: JsonPropertyName("file")] QqSongDetailFile? File);
    private sealed record QqSongDetailFile([property: JsonPropertyName("media_mid")] string? MediaMid);
}
