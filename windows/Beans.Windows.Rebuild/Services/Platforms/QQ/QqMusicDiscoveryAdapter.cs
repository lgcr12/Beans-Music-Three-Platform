using System.Net.Http.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.QQ.Dto;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

public sealed class QqMusicDiscoveryAdapter : IPlatformDiscoveryAdapter
{
    private static readonly Uri RankingsEndpoint = new("https://c.y.qq.com/v8/fcg-bin/fcg_myqq_toplist.fcg?format=json");
    private static readonly Uri MusicuEndpoint = new("https://u.y.qq.com/cgi-bin/musicu.fcg?format=json");
    private static readonly Uri PlaylistSquareEndpoint = new("https://c.y.qq.com/splcloud/fcgi-bin/fcg_get_diss_by_tag.fcg?picmid=1&g_tk=5381&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=1&categoryId=10000000&sortId=5&sin=0&ein=11");
    private static readonly Uri PublicReferer = new("https://y.qq.com/");

    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformErrorMapper _errorMapper;
    private readonly IPlatformResponseParser<QqRankingsPayload> _rankingsParser;
    private readonly IPlatformResponseParser<QqPlaylistsPayload> _playlistsParser;
    private readonly IPlatformResponseParser<QqPlaylistsPayload> _playlistSquareParser;
    private readonly IPlatformResponseParser<IReadOnlyList<MusicTrack>> _dailyParser;

    public QqMusicDiscoveryAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformJsonSerializer serializer,
        IPlatformErrorMapper errorMapper)
    {
        _httpClient = httpClientFactory.Get("qq");
        _errorMapper = errorMapper;
        _rankingsParser = new JsonPlatformResponseParser<QqRankingsEnvelope, QqRankingsPayload>(serializer, ParseRankings);
        _playlistsParser = new JsonPlatformResponseParser<QqRecommendEnvelope, QqPlaylistsPayload>(serializer, ParsePlaylists);
        _playlistSquareParser = new JsonPlatformResponseParser<QqPlaylistSquareEnvelope, QqPlaylistsPayload>(serializer, ParsePlaylistSquare);
        _dailyParser = new JsonPlatformResponseParser<QqRankingDetailEnvelope, IReadOnlyList<MusicTrack>>(serializer, ParseDailyTracks);
    }

    public string PlatformId => "qq";

    public DiscoveryAdapterCapabilities Capabilities { get; } = new(
        SupportsAnonymousHero: true,
        SupportsAnonymousRankings: true,
        SupportsAnonymousRecommendedPlaylists: true,
        SupportsAnonymousPlaylistSquare: false,
        SupportsAnonymousCategories: false,
        SupportsAuthenticatedDailyRecommendations: true,
        SupportsAuthenticatedUserPlaylists: false,
        SupportsSearch: false,
        SupportsPlayback: false);

    public async Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await SendPlaylistsAsync(context);
        if (!response.IsSuccess || response.Value is null)
        {
            return PlatformResponse<DiscoveryHero>.Failure(
                response.Error!, response.Elapsed, response.RetryCount, response.StatusCode, response.CacheKey) with
            {
                LoadedAt = response.LoadedAt
            };
        }

        var playlists = QqDiscoveryMapper.Playlists(response.Value);
        if (playlists.Count == 0)
        {
            var error = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.InvalidResponse,
                "QQ 音乐公开推荐歌单为空，无法生成精选内容");
            return PlatformResponse<DiscoveryHero>.Failure(
                error, response.Elapsed, response.RetryCount, response.StatusCode, response.CacheKey);
        }

        return PlatformResponse<DiscoveryHero>.Success(
            QqDiscoveryMapper.Hero(playlists), response.CorrelationId, response.Elapsed, response.RetryCount,
            response.DataOrigin, response.StatusCode, response.CacheKey, response.IsStale, response.SafeMessage) with
        {
            LoadedAt = response.LoadedAt
        };
    }

    public async Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _httpClient.SendAsync(CreateRankingsRequest, context, _rankingsParser);
        return Map(response, QqDiscoveryMapper.Rankings);
    }

    public async Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicRecommendedPlaylistsAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await SendPlaylistsAsync(context);
        return Map(response, QqDiscoveryMapper.Playlists);
    }

    public Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(
        string? category,
        int page,
        PlatformRequestContext context,
        CancellationToken cancellationToken) =>
        Unsupported<IReadOnlyList<MusicPlaylist>>(context, cancellationToken, "参考实现未提供 QQ 匿名歌单广场接口");

    public async Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var perChart = 12;
        var tracks = new List<MusicTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PlatformResponse<IReadOnlyList<MusicTrack>>? lastFailure = null;
        foreach (var topId in new[] { 26, 27, 62 })
        {
            var endpoint = new Uri($"https://c.y.qq.com/v8/fcg-bin/fcg_v8_toplist_cp.fcg?format=json&page=detail&type=top&topid={topId}&song_begin=0&song_num={perChart}");
            var response = await _httpClient.SendAsync(() => CreateGet(endpoint), context, _dailyParser);
            if (!response.IsSuccess || response.Value is null) { lastFailure = response; continue; }
            foreach (var track in response.Value)
            {
                var key = $"{track.Identity.Platform}:{track.Identity.NativeId}";
                if (seen.Add(key)) tracks.Add(track);
            }
        }
        if (tracks.Count == 0)
        {
            return lastFailure ?? PlatformResponse<IReadOnlyList<MusicTrack>>.Failure(
                _errorMapper.FromCode("qq", context.CorrelationId, PlatformErrorCode.InvalidResponse, "QQ 音乐每日推荐暂无可用歌曲"),
                TimeSpan.Zero, cacheKey: context.RequestKey);
        }
        var day = DateTime.UtcNow.DayOfYear;
        var shuffled = tracks.OrderBy(track => StableDayKey(track.Identity.NativeId, day)).Take(30).ToArray();
        return PlatformResponse<IReadOnlyList<MusicTrack>>.Success(
            shuffled, context.CorrelationId, TimeSpan.Zero, origin: DiscoveryDataOrigin.Live,
            cacheKey: context.RequestKey, safeMessage: "QQ 音乐每日推荐已更新");
    }

    public Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken) =>
        Unsupported<IReadOnlyList<string>>(context, cancellationToken, "参考实现未提供 QQ 匿名歌单分类接口");

    private async Task<PlatformResponse<QqPlaylistsPayload>> SendPlaylistsAsync(PlatformRequestContext context)
    {
        var primary = await _httpClient.SendAsync(CreateRecommendedPlaylistsRequest, context, _playlistsParser).ConfigureAwait(false);
        if (primary.IsSuccess && primary.Value is { Items.Count: > 0 }) return primary;

        // QQ retired the anonymous recommendation method for some regions. The public
        // playlist-square endpoint is still anonymous and carries the same playlist metadata.
        var fallback = await _httpClient.SendAsync(CreatePlaylistSquareRequest, context, _playlistSquareParser).ConfigureAwait(false);
        if (fallback.IsSuccess && fallback.Value is { Items.Count: > 0 })
        {
            return fallback with { SafeMessage = "QQ 音乐公开歌单已更新" };
        }

        return primary.IsSuccess ? fallback : primary;
    }

    private static HttpRequestMessage CreateRankingsRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, RankingsEndpoint);
        request.Headers.Referrer = PublicReferer;
        return request;
    }

    private static HttpRequestMessage CreateRecommendedPlaylistsRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MusicuEndpoint);
        request.Headers.Referrer = PublicReferer;
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        request.Content = JsonContent.Create(new
        {
            comm = new { ct = 24, cv = 0 },
            req_1 = new
            {
                module = "music.srfDissInfo.RecommendPlaylist",
                method = "GetRecommendPlaylist",
                param = new { uin = 0, lastDissid = 0, songtype = 1, scene = 0 }
            }
        });
        return request;
    }

    private static HttpRequestMessage CreateGet(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Referrer = PublicReferer;
        return request;
    }

    private static HttpRequestMessage CreatePlaylistSquareRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PlaylistSquareEndpoint);
        request.Headers.Referrer = PublicReferer;
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        return request;
    }

    private static PlatformParseResult<IReadOnlyList<MusicTrack>> ParseDailyTracks(QqRankingDetailEnvelope envelope)
    {
        if (envelope.Code is not null and not 0)
            return PlatformParseResult<IReadOnlyList<MusicTrack>>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐每日推荐业务请求失败");
        var raw = (envelope.Songs ?? [])
            .Select(item => item.Data ?? item.SongInfo)
            .Where(item => item is not null)
            .Cast<QqDetailTrackDto>()
            .ToArray();
        var tracks = raw.Select(MapDailyTrack).Where(item => item is not null).Cast<MusicTrack>().ToArray();
        return tracks.Length == 0
            ? PlatformParseResult<IReadOnlyList<MusicTrack>>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐每日推荐缺少歌曲数据")
            : PlatformParseResult<IReadOnlyList<MusicTrack>>.Success(tracks);
    }

    private static MusicTrack? MapDailyTrack(QqDetailTrackDto value)
    {
        var id = FirstNonEmpty(value.Mid, value.SongMid);
        var title = FirstNonEmpty(value.Name, value.SongName);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        var artists = (value.Singers ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new MusicArtist(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.QqMusic, FirstNonEmpty(item.Mid, item.Name)), item.Name!.Trim(), null))
            .ToArray();
        if (artists.Length == 0) artists = [new MusicArtist(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.QqMusic, "unknown"), "未知歌手", null)];
        var albumId = FirstNonEmpty(value.Album?.Mid, value.AlbumMid);
        var albumTitle = FirstNonEmpty(value.Album?.Name, value.AlbumName, "未知专辑");
        var album = new MusicAlbum(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.QqMusic, string.IsNullOrWhiteSpace(albumId) ? "unknown" : albumId), albumTitle,
            artists, string.IsNullOrWhiteSpace(albumId) ? null : new Uri($"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumId}.jpg"), 0);
        var file = value.File;
        var quality = file?.SizeFlac > 0 || file?.SizeApe > 0 ? AudioQuality.Lossless : file?.Size320 > 0 ? AudioQuality.High : AudioQuality.Standard;
        var available = file is not null && (file.Size128 > 0 || file.Size320 > 0 || file.SizeFlac > 0 || file.SizeApe > 0);
        return new MusicTrack(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.QqMusic, id), title, artists, album, album.Cover,
            value.DurationSeconds is > 0 ? TimeSpan.FromSeconds(value.DurationSeconds.Value) : TimeSpan.Zero,
            available ? AvailabilityState.Available : AvailabilityState.Unavailable, quality, value);
    }

    private static int StableDayKey(string value, int day)
    {
        unchecked
        {
            var hash = day * 397;
            foreach (var character in value) hash = hash * 31 + character;
            return hash & int.MaxValue;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static PlatformParseResult<QqRankingsPayload> ParseRankings(QqRankingsEnvelope envelope)
    {
        if (envelope.Code is not null and not 0)
            return PlatformParseResult<QqRankingsPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐排行榜业务请求失败");
        var items = envelope.Data?.TopList ?? envelope.RootTopList;
        if (items is null)
            return PlatformParseResult<QqRankingsPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐排行榜缺少数据节点");
        if (items.Any(item => item.Id is null or <= 0 || string.IsNullOrWhiteSpace(item.TopTitle ?? item.Title)))
            return PlatformParseResult<QqRankingsPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐排行榜缺少必需字段");
        return PlatformParseResult<QqRankingsPayload>.Success(new QqRankingsPayload(items));
    }

    private static PlatformParseResult<QqPlaylistsPayload> ParsePlaylists(QqRecommendEnvelope envelope)
    {
        if (envelope.Code is not null and not 0 || envelope.Request?.Code is not null and not 0)
            return PlatformParseResult<QqPlaylistsPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐推荐歌单业务请求失败");
        var items = envelope.Request?.Data?.Playlists;
        if (items is null)
            return PlatformParseResult<QqPlaylistsPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐推荐歌单缺少数据节点");
        if (items.Any(item => (item.Tid ?? item.Id) is null or <= 0 || string.IsNullOrWhiteSpace(item.Title)))
            return PlatformParseResult<QqPlaylistsPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐推荐歌单缺少必需字段");
        return PlatformParseResult<QqPlaylistsPayload>.Success(new QqPlaylistsPayload(items));
    }

    private static PlatformParseResult<QqPlaylistsPayload> ParsePlaylistSquare(QqPlaylistSquareEnvelope envelope)
    {
        if (envelope.Code is not null and not 0)
            return PlatformParseResult<QqPlaylistsPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐公开歌单请求失败");

        var items = (envelope.Data?.Items ?? [])
            .Where(item => long.TryParse(item.DissId, out var id) && id > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new QqPlaylistDto(
                long.Parse(item.DissId!), null, item.Name, item.ImageUrl, null, item.SongCount,
                item.PlayCount, item.Creator is null ? null : new QqCreatorDto(item.Creator.Name, null), null))
            .ToArray();
        return PlatformParseResult<QqPlaylistsPayload>.Success(new QqPlaylistsPayload(items));
    }

    private Task<PlatformResponse<T>> Unsupported<T>(
        PlatformRequestContext context,
        CancellationToken cancellationToken,
        string safeMessage)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.Cancelled);
            return Task.FromResult(PlatformResponse<T>.Failure(cancelled, TimeSpan.Zero, cacheKey: context.RequestKey));
        }

        var error = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.Unsupported, safeMessage);
        return Task.FromResult(PlatformResponse<T>.Failure(error, TimeSpan.Zero, cacheKey: context.RequestKey));
    }

    private static PlatformResponse<TResult> Map<TSource, TResult>(
        PlatformResponse<TSource> response,
        Func<TSource, TResult> map)
    {
        if (!response.IsSuccess || response.Value is null)
        {
            return PlatformResponse<TResult>.Failure(
                response.Error!, response.Elapsed, response.RetryCount, response.StatusCode, response.CacheKey) with
            {
                LoadedAt = response.LoadedAt
            };
        }

        return PlatformResponse<TResult>.Success(
            map(response.Value), response.CorrelationId, response.Elapsed, response.RetryCount,
            response.DataOrigin, response.StatusCode, response.CacheKey, response.IsStale, response.SafeMessage) with
        {
            LoadedAt = response.LoadedAt
        };
    }
}
