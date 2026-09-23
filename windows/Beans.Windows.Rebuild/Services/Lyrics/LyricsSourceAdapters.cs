using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Networking;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Lyrics;

public sealed class LocalLyricsSourceAdapter(
    ILocalMusicCatalog catalog,
    ILrcFileResolver resolver,
    ILrcParser parser) : ILyricsSourceAdapter
{
    public PlatformId Platform => PlatformId.Local;

    public async Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var track = await catalog.GetTrackAsync(request.StableId, cancellationToken).ConfigureAwait(false);
        var audioPath = track?.NormalizedPath ?? request.AudioPath;
        var lyricPath = track?.LrcPath;

        if (string.IsNullOrWhiteSpace(lyricPath) || !File.Exists(lyricPath))
        {
            if (string.IsNullOrWhiteSpace(audioPath)) return LyricsResult.NotFound();
            (lyricPath, _) = await resolver.ResolveAsync(audioPath, cancellationToken).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(lyricPath) || !File.Exists(lyricPath)) return LyricsResult.NotFound();

        try
        {
            var content = await LocalLrcFileResolver.ReadAsync(lyricPath, cancellationToken).ConfigureAwait(false);
            var document = parser.Parse(content, PlatformId.Local, cancellationToken);
            return document.Lines.Count == 0 ? LyricsResult.Empty() : LyricsResult.Loaded(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return LyricsResult.Error("本地歌词暂时无法读取，请检查文件后重试");
        }
    }
}

public sealed class QqLyricsSourceAdapter : ILyricsSourceAdapter
{
    private static readonly Uri Endpoint = new("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg");
    private static readonly Uri Referer = new("https://y.qq.com/portal/player.html");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformResponseParser<OnlineLyricsPayload> _responseParser;
    private readonly ILrcParser _lrcParser;

    public QqLyricsSourceAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformJsonSerializer serializer,
        ILrcParser lrcParser)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        _httpClient = httpClientFactory.Get("qq");
        _lrcParser = lrcParser ?? throw new ArgumentNullException(nameof(lrcParser));
        _responseParser = new JsonPlatformResponseParser<QqLyricsEnvelope, OnlineLyricsPayload>(serializer, ParseResponse);
    }

    public PlatformId Platform => PlatformId.QqMusic;

    public async Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OnlineLyricsRequestValidator.IsQqSongMid(request.NativeId))
            return LyricsResult.Error("QQ 音乐歌曲标识无效，无法加载歌词");

        var context = PlatformRequestContext.PublicDiscovery(
            "qq", "qq.lyrics.get", OnlineLyricsRequestValidator.RequestKey("qq", request.NativeId),
            cancellationToken, TimeSpan.FromSeconds(10));
        var response = await _httpClient.SendAsync(
            () => CreateRequest(request.NativeId), context, _responseParser).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return OnlineLyricsResultMapper.Map(response, _lrcParser, PlatformId.QqMusic, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(string songMid)
    {
        var query = $"songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=1&g_tk=5381";
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Endpoint}?{query}"));
        request.Headers.Referrer = Referer;
        return request;
    }

    private static PlatformParseResult<OnlineLyricsPayload> ParseResponse(QqLyricsEnvelope envelope)
    {
        if (envelope.Code is null && envelope.RetCode is null && envelope.SubCode is null)
            return PlatformParseResult<OnlineLyricsPayload>.Failure(
                PlatformErrorCode.InvalidResponse, "QQ 音乐歌词响应缺少状态字段");
        if (envelope.Code is not null and not 0 || envelope.RetCode is not null and not 0 || envelope.SubCode is not null and not 0)
            return PlatformParseResult<OnlineLyricsPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐歌词请求未成功");

        var lyric = Decode(envelope.Lyric);
        return PlatformParseResult<OnlineLyricsPayload>.Success(new OnlineLyricsPayload(
            lyric, null, string.IsNullOrWhiteSpace(lyric), false));
    }

    private static string? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var decoded = WebUtility.HtmlDecode(value).Trim();
        return decoded.Length == 0 ? null : decoded;
    }
}

public sealed class NetEaseLyricsSourceAdapter : ILyricsSourceAdapter
{
    private static readonly Uri Endpoint = new("https://music.163.com/api/song/lyric");
    private static readonly Uri Referer = new("https://music.163.com/");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformResponseParser<OnlineLyricsPayload> _responseParser;
    private readonly ILrcParser _lrcParser;

    public NetEaseLyricsSourceAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformJsonSerializer serializer,
        ILrcParser lrcParser)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        _httpClient = httpClientFactory.Get("netease");
        _lrcParser = lrcParser ?? throw new ArgumentNullException(nameof(lrcParser));
        _responseParser = new JsonPlatformResponseParser<NetEaseLyricsEnvelope, OnlineLyricsPayload>(serializer, ParseResponse);
    }

    public PlatformId Platform => PlatformId.NetEaseMusic;

    public async Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OnlineLyricsRequestValidator.TryGetNetEaseSongId(request.NativeId, out var songId))
            return LyricsResult.Error("网易云音乐歌曲标识无效，无法加载歌词");

        var context = PlatformRequestContext.PublicDiscovery(
            "netease", "netease.lyrics.get", OnlineLyricsRequestValidator.RequestKey("netease", request.NativeId),
            cancellationToken, TimeSpan.FromSeconds(10));
        var response = await _httpClient.SendAsync(
            () => CreateRequest(songId), context, _responseParser).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return OnlineLyricsResultMapper.Map(response, _lrcParser, PlatformId.NetEaseMusic, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(long songId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Endpoint}?id={songId}&lv=-1&kv=-1&tv=-1"));
        request.Headers.Referrer = Referer;
        return request;
    }

    private static PlatformParseResult<OnlineLyricsPayload> ParseResponse(NetEaseLyricsEnvelope envelope)
    {
        if (envelope.Code is not 200)
            return PlatformParseResult<OnlineLyricsPayload>.Failure(
                envelope.Code is 404 ? PlatformErrorCode.NotFound : PlatformErrorCode.ServiceUnavailable,
                envelope.Code is 404 ? "网易云音乐没有找到当前歌曲歌词" : "网易云音乐歌词请求未成功");

        var lyric = Normalize(envelope.Lrc?.Lyric);
        var translation = Normalize(envelope.TranslatedLyric?.Lyric);
        var instrumental = envelope.PureMusic == true;
        var notFound = envelope.NoLyric == true || envelope.Uncollected == true || string.IsNullOrWhiteSpace(lyric);
        return PlatformParseResult<OnlineLyricsPayload>.Success(
            new OnlineLyricsPayload(lyric, translation, notFound, instrumental));
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}

internal sealed record OnlineLyricsPayload(
    string? Lyrics,
    string? Translation,
    bool IsNotFound,
    bool IsInstrumental);

internal sealed record QqLyricsEnvelope(
    [property: JsonPropertyName("retcode")] int? RetCode,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("subcode")] int? SubCode,
    [property: JsonPropertyName("lyric")] string? Lyric);

internal sealed record NetEaseLyricsText(
    [property: JsonPropertyName("lyric")] string? Lyric);

internal sealed record NetEaseLyricsEnvelope(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("nolyric")] bool? NoLyric,
    [property: JsonPropertyName("uncollected")] bool? Uncollected,
    [property: JsonPropertyName("pureMusic")] bool? PureMusic,
    [property: JsonPropertyName("lrc")] NetEaseLyricsText? Lrc,
    [property: JsonPropertyName("tlyric")] NetEaseLyricsText? TranslatedLyric);

internal static class OnlineLyricsRequestValidator
{
    public static bool IsQqSongMid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 5 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character));

    public static bool TryGetNetEaseSongId(string? value, out long id) =>
        long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;

    public static string RequestKey(string platform, string nativeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nativeId));
        return $"{platform}:lyrics:{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }
}

internal static class OnlineLyricsResultMapper
{
    public static LyricsResult Map(
        PlatformResponse<OnlineLyricsPayload> response,
        ILrcParser parser,
        PlatformId platform,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.IsSuccess || response.Value is null)
        {
            return response.Error?.Code switch
            {
                PlatformErrorCode.NotFound => LyricsResult.NotFound(),
                PlatformErrorCode.Unsupported => LyricsResult.Unsupported($"当前版本暂不支持{platform.ToDisplayName()}在线歌词"),
                _ => LyricsResult.Error($"{platform.ToDisplayName()}歌词暂时无法加载，请稍后重试")
            };
        }

        var payload = response.Value;
        if (payload.IsInstrumental)
            return LyricsResult.NotFound("当前歌曲为纯音乐");
        if (payload.IsNotFound || string.IsNullOrWhiteSpace(payload.Lyrics))
            return LyricsResult.NotFound();

        var document = parser.Parse(payload.Lyrics, platform, cancellationToken);
        if (document.Lines.Count == 0)
            return LyricsResult.Empty();
        if (!string.IsNullOrWhiteSpace(payload.Translation))
            document = MergeTranslation(document, parser.Parse(payload.Translation, platform, cancellationToken));
        return LyricsResult.Loaded(document);
    }

    private static LyricDocument MergeTranslation(LyricDocument lyrics, LyricDocument translation)
    {
        if (translation.Lines.Count == 0) return lyrics;

        var byTimestamp = translation.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .GroupBy(line => line.Timestamp + translation.Offset - lyrics.Offset)
            .ToDictionary(group => group.Key, group => group.First().Text);
        var lines = lyrics.Lines
            .Select(line => byTimestamp.TryGetValue(line.Timestamp, out var translated)
                ? line with { Translation = translated }
                : line)
            .ToArray();
        return lyrics with { Lines = lines };
    }
}
