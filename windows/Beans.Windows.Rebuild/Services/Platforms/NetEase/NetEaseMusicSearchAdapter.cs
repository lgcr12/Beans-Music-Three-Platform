using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto.Search;
using Beans.Windows.Rebuild.Services.Search;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public sealed record NetEaseSearchCapabilities(
    bool SupportsAnonymousTracks,
    bool SupportsAnonymousAlbums,
    bool SupportsAnonymousArtists,
    bool SupportsAnonymousPlaylists,
    bool SupportsAnonymousSuggestions);

public sealed class NetEaseMusicSearchAdapter : IPlatformSearchAdapter
{
    private static readonly Uri SearchEndpoint = new("https://music.163.com/weapi/cloudsearch/pc");
    private static readonly Uri PublicReferer = new("https://music.163.com/");

    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformErrorMapper _errorMapper;
    private readonly IDiscoveryCache _cache;
    private readonly IPlatformResponseParser<NetEaseTrackSearchPayload> _trackParser;
    private readonly IPlatformResponseParser<NetEaseAlbumSearchPayload> _albumParser;
    private readonly IPlatformResponseParser<NetEaseArtistSearchPayload> _artistParser;
    private readonly TimeSpan _searchLifetime;

    public NetEaseMusicSearchAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformJsonSerializer serializer,
        IPlatformErrorMapper errorMapper,
        IDiscoveryCache cache,
        TimeSpan? searchLifetime = null)
    {
        _httpClient = httpClientFactory.Get("netease");
        _errorMapper = errorMapper;
        _cache = cache;
        _searchLifetime = searchLifetime ?? TimeSpan.FromMinutes(10);
        _trackParser = new JsonPlatformResponseParser<NetEaseTrackSearchResponse, NetEaseTrackSearchPayload>(serializer, ParseTracks);
        _albumParser = new JsonPlatformResponseParser<NetEaseAlbumSearchResponse, NetEaseAlbumSearchPayload>(serializer, ParseAlbums);
        _artistParser = new JsonPlatformResponseParser<NetEaseArtistSearchResponse, NetEaseArtistSearchPayload>(serializer, ParseArtists);
    }

    public PlatformId Platform => PlatformId.NetEaseMusic;
    public bool IsEnabled => true;
    public NetEaseSearchCapabilities Capabilities { get; } = new(true, true, true, false, false);

    public async Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var keyword = NormalizeKeyword(query.Keyword);
        if (keyword.Length == 0)
            return Empty(started, "请输入有效的搜索关键词");
        if (query.Filter == SearchResultFilter.Playlists)
            return Unsupported(started, "参考实现未提供网易云音乐匿名歌单搜索接口");

        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var offset = Math.Max(query.Offset, 0);
        var page = offset / pageSize + 1;
        var cacheKey = NetEaseSearchRequestKeys.Search(query.Filter, keyword, page, pageSize);
        var storageKey = NetEaseSearchRequestKeys.Storage(cacheKey);
        if (!query.ForceRefresh && TryCached(storageKey, false, out var fresh))
            return FromCache(fresh!, SearchDataOrigin.CacheFresh, started);

        var attempt = query.Filter switch
        {
            SearchResultFilter.Tracks => await SearchTracksAsync(keyword, page, offset, pageSize, cancellationToken),
            SearchResultFilter.Albums => await SearchAlbumsAsync(keyword, page, offset, pageSize, cancellationToken),
            SearchResultFilter.Artists => await SearchArtistsAsync(keyword, page, offset, pageSize, cancellationToken),
            _ => await SearchAllAsync(keyword, page, offset, pageSize, cancellationToken)
        };
        cancellationToken.ThrowIfCancellationRequested();

        if (attempt.IsSuccess && attempt.Payload is not null && !attempt.Payload.IsPartialSuccess)
        {
            _cache.SetValue(storageKey, attempt.Payload, _searchLifetime);
            return FromLive(attempt.Payload, started);
        }

        if (!attempt.IsSuccess || attempt.Payload?.IsPartialSuccess == true)
        {
            if (TryCached(storageKey, false, out fresh))
                return FromCache(fresh!, SearchDataOrigin.CacheFresh, started);
            if (TryCached(storageKey, true, out var stale) && stale!.IsStale)
                return FromCache(stale, SearchDataOrigin.CacheStale, started);
        }

        if (attempt.IsSuccess && attempt.Payload is not null)
            return FromLive(attempt.Payload, started);

        return Failure(attempt.Error, started);
    }

    public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
    }

    private async Task<SearchAttempt> SearchAllAsync(
        string keyword, int page, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var attempts = await Task.WhenAll(
            SearchTracksAsync(keyword, page, offset, pageSize, cancellationToken),
            SearchAlbumsAsync(keyword, page, offset, pageSize, cancellationToken),
            SearchArtistsAsync(keyword, page, offset, pageSize, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        var successful = attempts.Where(item => item.IsSuccess && item.Payload is not null).Select(item => item.Payload!).ToArray();
        if (successful.Length == 0)
            return SearchAttempt.Failure(PreferredError(attempts.Select(item => item.Error)));

        return SearchAttempt.Success(new NetEaseSearchCachePayload(
            successful.SelectMany(item => item.Items).ToArray(),
            successful.Sum(item => item.TotalCount),
            successful.Any(item => item.HasMore),
            successful.Length != attempts.Length));
    }

    private async Task<SearchAttempt> SearchTracksAsync(
        string keyword, int page, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var key = NetEaseSearchRequestKeys.Search(SearchResultFilter.Tracks, keyword, page, pageSize);
        var response = await _httpClient.SendAsync(
            () => CreateSearchRequest(keyword, 1, offset, pageSize), Context("tracks", key, cancellationToken), _trackParser);
        if (!response.IsSuccess || response.Value is null) return SearchAttempt.Failure(response.Error);
        var items = NetEaseSearchMapper.Tracks(response.Value);
        var total = Math.Max(response.Value.Result.TotalCount ?? items.Count, items.Count);
        return SearchAttempt.Success(new(items, total, HasMore(offset, items.Count, total), false));
    }

    private async Task<SearchAttempt> SearchAlbumsAsync(
        string keyword, int page, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var key = NetEaseSearchRequestKeys.Search(SearchResultFilter.Albums, keyword, page, pageSize);
        var response = await _httpClient.SendAsync(
            () => CreateSearchRequest(keyword, 10, offset, pageSize), Context("albums", key, cancellationToken), _albumParser);
        if (!response.IsSuccess || response.Value is null) return SearchAttempt.Failure(response.Error);
        var items = NetEaseSearchMapper.Albums(response.Value);
        var total = Math.Max(response.Value.Result.TotalCount ?? items.Count, items.Count);
        return SearchAttempt.Success(new(items, total, HasMore(offset, items.Count, total), false));
    }

    private async Task<SearchAttempt> SearchArtistsAsync(
        string keyword, int page, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var key = NetEaseSearchRequestKeys.Search(SearchResultFilter.Artists, keyword, page, pageSize);
        var response = await _httpClient.SendAsync(
            () => CreateSearchRequest(keyword, 100, offset, pageSize), Context("artists", key, cancellationToken), _artistParser);
        if (!response.IsSuccess || response.Value is null) return SearchAttempt.Failure(response.Error);
        var items = NetEaseSearchMapper.Artists(response.Value);
        var total = Math.Max(response.Value.Result.TotalCount ?? items.Count, items.Count);
        return SearchAttempt.Success(new(items, total, HasMore(offset, items.Count, total), false));
    }

    private static HttpRequestMessage CreateSearchRequest(string keyword, int type, int offset, int pageSize)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint);
        request.Headers.Referrer = PublicReferer;
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BeansMusic-Windows/1.0");
        request.Content = NetEaseWeapi.CreateContent(NetEaseSearchProtocol.CreatePayload(keyword, type, offset, pageSize));
        return request;
    }

    private static PlatformParseResult<NetEaseTrackSearchPayload> ParseTracks(NetEaseTrackSearchResponse envelope)
    {
        var error = BusinessError<NetEaseTrackSearchPayload>(envelope.Code, "网易云音乐歌曲搜索业务请求失败");
        if (error is not null) return error.Value;
        if (envelope.Result?.TotalCount is null)
            return PlatformParseResult<NetEaseTrackSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌曲搜索缺少数据节点");
        if (envelope.Result.Songs is null)
        {
            if (envelope.Result.TotalCount == 0)
                return PlatformParseResult<NetEaseTrackSearchPayload>.Success(new(envelope.Result with { Songs = [] }));
            return PlatformParseResult<NetEaseTrackSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌曲搜索缺少结果列表");
        }
        if (envelope.Result.Songs.Any(item => item.Id is null or <= 0 || string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<NetEaseTrackSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌曲搜索缺少必需字段");
        return PlatformParseResult<NetEaseTrackSearchPayload>.Success(new(envelope.Result));
    }

    private static PlatformParseResult<NetEaseAlbumSearchPayload> ParseAlbums(NetEaseAlbumSearchResponse envelope)
    {
        var error = BusinessError<NetEaseAlbumSearchPayload>(envelope.Code, "网易云音乐专辑搜索业务请求失败");
        if (error is not null) return error.Value;
        if (envelope.Result?.TotalCount is null)
            return PlatformParseResult<NetEaseAlbumSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐专辑搜索缺少数据节点");
        if (envelope.Result.Albums is null)
        {
            if (envelope.Result.TotalCount == 0)
                return PlatformParseResult<NetEaseAlbumSearchPayload>.Success(new(envelope.Result with { Albums = [] }));
            return PlatformParseResult<NetEaseAlbumSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐专辑搜索缺少结果列表");
        }
        if (envelope.Result.Albums.Any(item => item.Id is null or <= 0 || string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<NetEaseAlbumSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐专辑搜索缺少必需字段");
        return PlatformParseResult<NetEaseAlbumSearchPayload>.Success(new(envelope.Result));
    }

    private static PlatformParseResult<NetEaseArtistSearchPayload> ParseArtists(NetEaseArtistSearchResponse envelope)
    {
        var error = BusinessError<NetEaseArtistSearchPayload>(envelope.Code, "网易云音乐歌手搜索业务请求失败");
        if (error is not null) return error.Value;
        if (envelope.Result?.TotalCount is null)
            return PlatformParseResult<NetEaseArtistSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌手搜索缺少数据节点");
        if (envelope.Result.Artists is null)
        {
            if (envelope.Result.TotalCount == 0)
                return PlatformParseResult<NetEaseArtistSearchPayload>.Success(new(envelope.Result with { Artists = [] }));
            return PlatformParseResult<NetEaseArtistSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌手搜索缺少结果列表");
        }
        if (envelope.Result.Artists.Any(item => item.Id is null or <= 0 || string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<NetEaseArtistSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐歌手搜索缺少必需字段");
        return PlatformParseResult<NetEaseArtistSearchPayload>.Success(new(envelope.Result));
    }

    private static PlatformParseResult<T>? BusinessError<T>(int? code, string message)
    {
        if (code == 200) return null;
        if (code is null)
            return PlatformParseResult<T>.Failure(PlatformErrorCode.InvalidResponse, "网易云音乐搜索缺少业务状态");
        var mapped = code is 301 or 302 or 401 ? PlatformErrorCode.Unauthorized : PlatformErrorCode.ServiceUnavailable;
        return PlatformParseResult<T>.Failure(mapped, mapped == PlatformErrorCode.Unauthorized ? "登录网易云音乐后搜索该内容" : message);
    }

    private static PlatformRequestContext Context(string operation, string requestKey, CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery("netease", $"netease.search.{operation}", requestKey, cancellationToken, TimeSpan.FromSeconds(10));

    private bool TryCached(
        DiscoveryCacheKey key, bool includeExpired, out DiscoveryCacheValue<NetEaseSearchCachePayload>? cached) =>
        _cache.TryGetValue(key, includeExpired, out cached);

    private static SearchAdapterResponse FromLive(NetEaseSearchCachePayload payload, DateTimeOffset started) =>
        BuildResponse(payload, SearchDataOrigin.Live, DateTimeOffset.UtcNow - started, DateTimeOffset.UtcNow);

    private static SearchAdapterResponse FromCache(
        DiscoveryCacheValue<NetEaseSearchCachePayload> cached, SearchDataOrigin origin, DateTimeOffset started) =>
        BuildResponse(cached.Value, origin, DateTimeOffset.UtcNow - started, cached.LoadedAt);

    private static SearchAdapterResponse BuildResponse(
        NetEaseSearchCachePayload payload, SearchDataOrigin origin, TimeSpan elapsed, DateTimeOffset loadedAt)
    {
        var items = payload.Items.Select(item => item with { DataOrigin = origin }).ToArray();
        var message = origin switch
        {
            SearchDataOrigin.CacheFresh => "网易云音乐搜索结果来自缓存",
            SearchDataOrigin.CacheStale => "网易云音乐网络不可用，正在显示上次搜索结果",
            _ when payload.IsPartialSuccess => "网易云音乐部分搜索类型暂时不可用",
            _ when items.Length == 0 => "网易云音乐没有匹配结果",
            _ => "网易云音乐搜索完成"
        };
        return new(PlatformId.NetEaseMusic, items, payload.TotalCount, payload.HasMore,
            items.Length == 0 ? SearchSourceState.Empty : SearchSourceState.Succeeded,
            origin, message, false, elapsed, payload.IsPartialSuccess, LoadedAt: loadedAt);
    }

    private SearchAdapterResponse Failure(PlatformError? error, DateTimeOffset started)
    {
        var state = error?.Code switch
        {
            PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired => SearchSourceState.Unauthorized,
            PlatformErrorCode.Unsupported => SearchSourceState.Unsupported,
            _ => SearchSourceState.Error
        };
        return new(PlatformId.NetEaseMusic, [], 0, false, state, SearchDataOrigin.Live,
            state == SearchSourceState.Unauthorized ? "登录网易云音乐后搜索该内容" : "网易云音乐搜索暂时不可用",
            error?.RequiresLogin == true, DateTimeOffset.UtcNow - started, ErrorCode: error?.Code, LoadedAt: DateTimeOffset.UtcNow);
    }

    private static SearchAdapterResponse Empty(DateTimeOffset started, string message) =>
        new(PlatformId.NetEaseMusic, [], 0, false, SearchSourceState.Empty, SearchDataOrigin.Live,
            message, false, DateTimeOffset.UtcNow - started, LoadedAt: DateTimeOffset.UtcNow);

    private static SearchAdapterResponse Unsupported(DateTimeOffset started, string message) =>
        new(PlatformId.NetEaseMusic, [], 0, false, SearchSourceState.Unsupported, SearchDataOrigin.Live,
            message, false, DateTimeOffset.UtcNow - started, ErrorCode: PlatformErrorCode.Unsupported, LoadedAt: DateTimeOffset.UtcNow);

    private PlatformError PreferredError(IEnumerable<PlatformError?> errors)
    {
        var values = errors.Where(error => error is not null).Cast<PlatformError>().ToArray();
        return values.FirstOrDefault(error => error.Code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired)
               ?? values.FirstOrDefault()
               ?? _errorMapper.FromCode("netease", Guid.NewGuid().ToString("N"), PlatformErrorCode.Unknown);
    }

    private static bool HasMore(int offset, int count, int total) => count > 0 && offset + count < total;

    private static string NormalizeKeyword(string keyword)
    {
        var value = new string(keyword.Trim().Where(character => !char.IsControl(character)).ToArray());
        return value.Length <= 80 ? value : value[..80];
    }

    private sealed record NetEaseSearchCachePayload(
        IReadOnlyList<SearchResultItem> Items,
        int TotalCount,
        bool HasMore,
        bool IsPartialSuccess);

    private sealed record SearchAttempt(bool IsSuccess, NetEaseSearchCachePayload? Payload, PlatformError? Error)
    {
        public static SearchAttempt Success(NetEaseSearchCachePayload payload) => new(true, payload, null);
        public static SearchAttempt Failure(PlatformError? error) => new(false, null, error);
    }
}
