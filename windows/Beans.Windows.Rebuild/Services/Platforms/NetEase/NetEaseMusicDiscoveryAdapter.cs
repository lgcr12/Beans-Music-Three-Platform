using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto;
using Beans.Windows.Rebuild.Services.Security;
using Beans.Windows.Rebuild.Services.Playback;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public sealed class NetEaseMusicDiscoveryAdapter : IPlatformDiscoveryAdapter
{
    private static readonly Uri RankingsEndpoint = new("https://music.163.com/weapi/toplist/detail");
    private static readonly Uri RecommendedEndpoint = new("https://music.163.com/weapi/playlist/highquality/list");
    private static readonly Uri PlaylistSquareEndpoint = new("https://music.163.com/weapi/playlist/list");
    private static readonly Uri CategoriesEndpoint = new("https://music.163.com/weapi/playlist/catlist");
    private static readonly Uri DailyRecommendationsEndpoint = new("https://music.163.com/api/v3/discovery/recommend/songs");
    private static readonly Uri PublicReferer = new("https://music.163.com/");
    private const int PageSize = 18;

    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformErrorMapper _errorMapper;
    private readonly IPlatformResponseParser<NetEaseRankingsPayload> _rankingsParser;
    private readonly IPlatformResponseParser<NetEasePlaylistsPayload> _playlistsParser;
    private readonly IPlatformResponseParser<NetEaseCategoriesPayload> _categoriesParser;
    private readonly IPlatformResponseParser<IReadOnlyList<MusicTrack>> _dailyParser;
    private readonly ISecureCredentialStore? _credentials;

    public NetEaseMusicDiscoveryAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformJsonSerializer serializer,
        IPlatformErrorMapper errorMapper,
        ISecureCredentialStore? credentials = null)
    {
        _httpClient = httpClientFactory.Get("netease");
        _errorMapper = errorMapper;
        _rankingsParser = new JsonPlatformResponseParser<NetEaseTopListEnvelope, NetEaseRankingsPayload>(serializer, ParseRankings);
        _playlistsParser = new JsonPlatformResponseParser<NetEasePlaylistEnvelope, NetEasePlaylistsPayload>(serializer, ParsePlaylists);
        _categoriesParser = new JsonPlatformResponseParser<NetEaseCategoryEnvelope, NetEaseCategoriesPayload>(serializer, ParseCategories);
        _dailyParser = new JsonPlatformResponseParser<NetEaseDailyRecommendationsEnvelope, IReadOnlyList<MusicTrack>>(serializer, ParseDailyRecommendations);
        _credentials = credentials;
    }

    public string PlatformId => "netease";

    public DiscoveryAdapterCapabilities Capabilities { get; } = new(
        SupportsAnonymousHero: true,
        SupportsAnonymousRankings: true,
        SupportsAnonymousRecommendedPlaylists: true,
        SupportsAnonymousPlaylistSquare: true,
        SupportsAnonymousCategories: true,
        SupportsAuthenticatedDailyRecommendations: true,
        SupportsAuthenticatedUserPlaylists: false,
        SupportsSearch: false,
        SupportsPlayback: false);

    public async Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await SendPlaylistsAsync(
            () => CreatePost(RecommendedEndpoint, new Dictionary<string, object?>
            {
                ["cat"] = "全部", ["limit"] = PageSize, ["offset"] = 0, ["total"] = true
            }), context);
        if (!response.IsSuccess || response.Value is null) return FailureFrom<NetEasePlaylistsPayload, DiscoveryHero>(response);
        var playlists = NetEaseDiscoveryMapper.Playlists(response.Value);
        if (playlists.Count == 0)
        {
            var error = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.InvalidResponse,
                "网易云音乐公开推荐歌单为空");
            return PlatformResponse<DiscoveryHero>.Failure(error, response.Elapsed, response.RetryCount, response.StatusCode, response.CacheKey);
        }
        return Map(response, _ => NetEaseDiscoveryMapper.Hero(playlists));
    }

    public async Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _httpClient.SendAsync(
            () => CreatePost(RankingsEndpoint, new Dictionary<string, object?>()), context, _rankingsParser);
        return Map(response, NetEaseDiscoveryMapper.Rankings);
    }

    public async Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicRecommendedPlaylistsAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await SendPlaylistsAsync(
            () => CreatePost(RecommendedEndpoint, new Dictionary<string, object?>
            {
                ["cat"] = "全部", ["limit"] = PageSize, ["offset"] = 0, ["total"] = true
            }), context);
        return Map(response, NetEaseDiscoveryMapper.Playlists);
    }

    public async Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(
        string? category,
        int page,
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page < 1)
        {
            var invalid = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.InvalidRequest);
            return PlatformResponse<IReadOnlyList<MusicPlaylist>>.Failure(invalid, TimeSpan.Zero, cacheKey: context.RequestKey);
        }
        var selectedCategory = string.IsNullOrWhiteSpace(category) ? "全部" : category.Trim();
        var offset = checked((page - 1) * PageSize);
        var response = await SendPlaylistsAsync(
            () => CreatePost(PlaylistSquareEndpoint, new Dictionary<string, object?>
            {
                ["cat"] = selectedCategory, ["order"] = "hot", ["limit"] = PageSize,
                ["offset"] = offset, ["total"] = true
            }), context);
        return Map(response, NetEaseDiscoveryMapper.Playlists);
    }

    public async Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _httpClient.SendAsync(
            () => CreatePost(CategoriesEndpoint, new Dictionary<string, object?>()), context, _categoriesParser);
        return Map(response, NetEaseDiscoveryMapper.Categories);
    }

    public async Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(
        PlatformRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _credentials is null ? null : await ProviderCredentialReader.ReadSessionAsync(_credentials, PlatformId, cancellationToken);
        if (session is null)
        {
            var error = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.Unauthorized, "登录网易云音乐后查看每日推荐");
            return PlatformResponse<IReadOnlyList<MusicTrack>>.Failure(error, TimeSpan.Zero, cacheKey: context.RequestKey);
        }
        var accountHash = AccountIdentityHasher.Hash(session);
        var authContext = new PlatformRequestContext(PlatformId, "netease.discovery.daily-recommendations",
            "netease:discovery:daily-recommendations", TimeSpan.FromSeconds(15), true,
            PlatformCachePolicy.NetworkOnly, accountHash, false, 0, cancellationToken);
        var response = await _httpClient.SendAsync(() => CreateAuthenticatedDailyRequest(session), authContext, _dailyParser);
        return response with { CacheKey = context.RequestKey };
    }

    private static HttpRequestMessage CreateAuthenticatedDailyRequest(string session)
    {
        var request = CreatePost(DailyRecommendationsEndpoint, new Dictionary<string, object?>());
        ProviderCredentialReader.ApplyCookieHeader(request, session);
        return request;
    }

    private static PlatformParseResult<IReadOnlyList<MusicTrack>> ParseDailyRecommendations(NetEaseDailyRecommendationsEnvelope envelope)
    {
        if (envelope.Code is not 200)
            return PlatformParseResult<IReadOnlyList<MusicTrack>>.Failure(
                envelope.Code is 301 or 401 or 403 ? PlatformErrorCode.Unauthorized : PlatformErrorCode.ServiceUnavailable,
                "网易云音乐每日推荐请求失败");
        var values = envelope.Data?.DailySongs ?? [];
        var tracks = values.Select(MapDailyTrack).Where(value => value is not null).Cast<MusicTrack>().ToArray();
        return PlatformParseResult<IReadOnlyList<MusicTrack>>.Success(tracks);
    }

    private static MusicTrack? MapDailyTrack(NetEaseDetailTrackDto value)
    {
        if (value.Id is not > 0 || string.IsNullOrWhiteSpace(value.Name)) return null;
        var artists = (value.Artists ?? value.LegacyArtists ?? [])
            .Where(item => item.Id is > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new MusicArtist(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.NetEaseMusic, item.Id!.Value.ToString()), item.Name!.Trim(), null))
            .ToArray();
        if (artists.Length == 0) artists = [new MusicArtist(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.NetEaseMusic, "unknown"), "未知歌手", null)];
        var albumRef = value.Album ?? value.LegacyAlbum;
        var albumId = albumRef?.Id is > 0 ? albumRef.Id.Value.ToString() : "unknown";
        var album = new MusicAlbum(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.NetEaseMusic, albumId), albumRef?.Name?.Trim() ?? "未知专辑", artists,
            Uri.TryCreate(albumRef?.PictureUrl, UriKind.Absolute, out var cover) ? cover : null, 0);
        var duration = value.DurationMilliseconds ?? value.LegacyDurationMilliseconds;
        var quality = value.Lossless?.Bitrate > 0 ? AudioQuality.Lossless : value.High?.Bitrate > 0 ? AudioQuality.High : AudioQuality.Standard;
        var unavailable = value.Privilege?.Status is < 0 || value.Fee is 1 or 4;
        return new MusicTrack(new MusicIdentity(global::Beans.Windows.Rebuild.Models.PlatformId.NetEaseMusic, value.Id.Value.ToString()), value.Name.Trim(), artists, album, album.Cover,
            duration is > 0 ? TimeSpan.FromMilliseconds(duration.Value) : TimeSpan.Zero,
            unavailable ? AvailabilityState.Unavailable : AvailabilityState.Available, quality, value);
    }

    private Task<PlatformResponse<NetEasePlaylistsPayload>> SendPlaylistsAsync(
        Func<HttpRequestMessage> requestFactory,
        PlatformRequestContext context) =>
        _httpClient.SendAsync(requestFactory, context, _playlistsParser);

    private static HttpRequestMessage CreatePost(Uri endpoint, IReadOnlyDictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Referrer = PublicReferer;
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BeansMusic-Windows/1.0");
        request.Content = NetEaseWeapi.CreateContent(payload);
        return request;
    }

    private static PlatformParseResult<NetEaseRankingsPayload> ParseRankings(NetEaseTopListEnvelope envelope)
    {
        var businessError = BusinessError<NetEaseRankingsPayload>(envelope.Code, "网易云音乐排行榜业务请求失败");
        if (businessError is not null) return businessError.Value;
        if (envelope.List is null)
            return PlatformParseResult<NetEaseRankingsPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐排行榜缺少数据节点");
        if (envelope.List.Any(item => item.Id is null or <= 0 || string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<NetEaseRankingsPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐排行榜缺少必需字段");
        return PlatformParseResult<NetEaseRankingsPayload>.Success(new NetEaseRankingsPayload(envelope.List));
    }

    private static PlatformParseResult<NetEasePlaylistsPayload> ParsePlaylists(NetEasePlaylistEnvelope envelope)
    {
        var businessError = BusinessError<NetEasePlaylistsPayload>(envelope.Code, "网易云音乐歌单业务请求失败");
        if (businessError is not null) return businessError.Value;
        if (envelope.Playlists is null)
            return PlatformParseResult<NetEasePlaylistsPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌单缺少数据节点");
        if (envelope.Playlists.Any(item => item.Id is null or <= 0 || string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<NetEasePlaylistsPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌单缺少必需字段");
        return PlatformParseResult<NetEasePlaylistsPayload>.Success(new NetEasePlaylistsPayload(envelope.Playlists));
    }

    private static PlatformParseResult<NetEaseCategoriesPayload> ParseCategories(NetEaseCategoryEnvelope envelope)
    {
        var businessError = BusinessError<NetEaseCategoriesPayload>(envelope.Code, "网易云音乐分类业务请求失败");
        if (businessError is not null) return businessError.Value;
        if (envelope.Sub is null)
            return PlatformParseResult<NetEaseCategoriesPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐分类缺少数据节点");
        var categories = envelope.Sub
            .Select(item => item.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return PlatformParseResult<NetEaseCategoriesPayload>.Success(new NetEaseCategoriesPayload(categories));
    }

    private static PlatformParseResult<T>? BusinessError<T>(int? code, string message)
    {
        if (code is null or 200) return null;
        var mapped = code is 301 or 302 or 401 ? PlatformErrorCode.Unauthorized : PlatformErrorCode.ServiceUnavailable;
        return PlatformParseResult<T>.Failure(mapped, mapped == PlatformErrorCode.Unauthorized ? "需要登录后继续" : message);
    }

    private static PlatformResponse<TResult> Map<TSource, TResult>(
        PlatformResponse<TSource> response,
        Func<TSource, TResult> map)
    {
        if (!response.IsSuccess || response.Value is null) return FailureFrom<TSource, TResult>(response);
        return PlatformResponse<TResult>.Success(
            map(response.Value), response.CorrelationId, response.Elapsed, response.RetryCount,
            response.DataOrigin, response.StatusCode, response.CacheKey, response.IsStale, response.SafeMessage) with
        {
            LoadedAt = response.LoadedAt
        };
    }

    private static PlatformResponse<TResult> FailureFrom<TSource, TResult>(PlatformResponse<TSource> response) =>
        PlatformResponse<TResult>.Failure(
            response.Error!, response.Elapsed, response.RetryCount, response.StatusCode, response.CacheKey) with
        {
            LoadedAt = response.LoadedAt
        };
}
