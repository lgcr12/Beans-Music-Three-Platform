using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.QQ.Dto.Search;
using Beans.Windows.Rebuild.Services.Search;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

public sealed record QqSearchCapabilities(
    bool SupportsAnonymousTracks,
    bool SupportsAnonymousAlbums,
    bool SupportsAnonymousArtists,
    bool SupportsAnonymousPlaylists,
    bool SupportsAnonymousSuggestions);

public sealed class QqMusicSearchAdapter : IPlatformSearchAdapter
{
    private static readonly Uri ClientSearchEndpoint = new("https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp");
    private static readonly Uri SmartboxEndpoint = new("https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg");
    private static readonly Uri PublicReferer = new("https://y.qq.com/portal/player.html");
    private readonly IPlatformHttpClient _httpClient;
    private readonly IPlatformErrorMapper _errorMapper;
    private readonly IDiscoveryCache _cache;
    private readonly IPlatformResponseParser<QqTrackSearchPayload> _trackParser;
    private readonly IPlatformResponseParser<QqAlbumSearchPayload> _albumParser;
    private readonly IPlatformResponseParser<QqArtistSearchPayload> _artistParser;
    private readonly IPlatformResponseParser<QqSuggestionPayload> _suggestionParser;
    private readonly TimeSpan _searchLifetime;
    private readonly TimeSpan _suggestionLifetime;

    public QqMusicSearchAdapter(
        IPlatformHttpClientFactory httpClientFactory,
        IPlatformJsonSerializer serializer,
        IPlatformErrorMapper errorMapper,
        IDiscoveryCache cache,
        TimeSpan? searchLifetime = null,
        TimeSpan? suggestionLifetime = null)
    {
        _httpClient = httpClientFactory.Get("qq");
        _errorMapper = errorMapper;
        _cache = cache;
        _searchLifetime = searchLifetime ?? TimeSpan.FromMinutes(10);
        _suggestionLifetime = suggestionLifetime ?? TimeSpan.FromMinutes(2);
        _trackParser = new JsonPlatformResponseParser<QqClientSearchEnvelope, QqTrackSearchPayload>(serializer, ParseTracks);
        _albumParser = new JsonPlatformResponseParser<QqClientSearchEnvelope, QqAlbumSearchPayload>(serializer, ParseAlbums);
        _artistParser = new JsonPlatformResponseParser<QqSmartboxEnvelope, QqArtistSearchPayload>(serializer, ParseArtists);
        _suggestionParser = new JsonPlatformResponseParser<QqSmartboxEnvelope, QqSuggestionPayload>(serializer, ParseSuggestions);
    }

    public PlatformId Platform => PlatformId.QqMusic;
    public bool IsEnabled => true;
    public QqSearchCapabilities Capabilities { get; } = new(true, true, true, false, true);

    public async Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var keyword = NormalizeKeyword(query.Keyword);
        if (keyword.Length == 0)
            return Empty(started, "请输入有效的搜索关键词");
        if (query.Filter == SearchResultFilter.Playlists)
            return Unsupported(started, "参考实现未提供 QQ 音乐匿名歌单搜索接口");

        var pageSize = Math.Clamp(query.PageSize, 1, 50);
        var offset = Math.Max(query.Offset, 0);
        var page = offset / pageSize + 1;
        var cacheKey = QqSearchRequestKeys.Search(query.Filter, keyword, page, pageSize);
        var storageKey = QqSearchRequestKeys.Storage(cacheKey);
        if (!query.ForceRefresh && TryCached(storageKey, false, out var fresh))
            return FromCache(fresh!, SearchDataOrigin.CacheFresh, started);

        var attempt = query.Filter switch
        {
            SearchResultFilter.Tracks => await SearchTracksAsync(keyword, page, pageSize, offset, cancellationToken),
            SearchResultFilter.Albums => await SearchAlbumsAsync(keyword, page, pageSize, offset, cancellationToken),
            SearchResultFilter.Artists => await SearchArtistsAsync(keyword, page, pageSize, offset, cancellationToken),
            _ => await SearchAllAsync(keyword, page, pageSize, offset, cancellationToken)
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

    public async Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeKeyword(keyword);
        if (normalized.Length < 2) return [];
        var requestKey = QqSearchRequestKeys.Suggestions(normalized);
        var storageKey = QqSearchRequestKeys.Storage(requestKey);
        if (_cache.TryGetValue<IReadOnlyList<SearchSuggestion>>(storageKey, false, out var fresh) && fresh is not null)
            return fresh.Value;

        var context = Context("suggestions", requestKey, cancellationToken);
        var response = await _httpClient.SendAsync(
            () => CreateSmartboxRequest(normalized), context, _suggestionParser);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccess && response.Value is not null)
        {
            var suggestions = QqSearchMapper.Suggestions(response.Value);
            _cache.SetValue(storageKey, suggestions, _suggestionLifetime);
            return suggestions;
        }

        return _cache.TryGetValue<IReadOnlyList<SearchSuggestion>>(storageKey, true, out var stale) && stale is not null
            ? stale.Value
            : [];
    }

    private async Task<SearchAttempt> SearchAllAsync(
        string keyword, int page, int pageSize, int offset, CancellationToken cancellationToken)
    {
        var tasks = new[]
        {
            SearchTracksAsync(keyword, page, pageSize, offset, cancellationToken),
            SearchAlbumsAsync(keyword, page, pageSize, offset, cancellationToken),
            SearchArtistsAsync(keyword, page, pageSize, offset, cancellationToken)
        };
        var attempts = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        var successful = attempts.Where(item => item.IsSuccess && item.Payload is not null).Select(item => item.Payload!).ToArray();
        if (successful.Length == 0)
            return SearchAttempt.Failure(PreferredError(attempts.Select(item => item.Error)));

        var payload = new QqSearchCachePayload(
            successful.SelectMany(item => item.Items).ToArray(),
            successful.Sum(item => item.TotalCount),
            successful.Any(item => item.HasMore),
            successful.Length != attempts.Length);
        return SearchAttempt.Success(payload);
    }

    private async Task<SearchAttempt> SearchTracksAsync(
        string keyword, int page, int pageSize, int offset, CancellationToken cancellationToken)
    {
        var key = QqSearchRequestKeys.Search(SearchResultFilter.Tracks, keyword, page, pageSize);
        var response = await _httpClient.SendAsync(
            () => CreateClientSearchRequest(keyword, page, pageSize, 0), Context("tracks", key, cancellationToken), _trackParser);
        if (!response.IsSuccess || response.Value is null) return SearchAttempt.Failure(response.Error);
        var items = QqSearchMapper.Tracks(response.Value);
        var total = Math.Max(response.Value.Page.TotalCount ?? items.Count, items.Count);
        return SearchAttempt.Success(new(items, total, HasMore(offset, items.Count, total), false));
    }

    private async Task<SearchAttempt> SearchAlbumsAsync(
        string keyword, int page, int pageSize, int offset, CancellationToken cancellationToken)
    {
        var key = QqSearchRequestKeys.Search(SearchResultFilter.Albums, keyword, page, pageSize);
        var response = await _httpClient.SendAsync(
            () => CreateClientSearchRequest(keyword, page, pageSize, 8), Context("albums", key, cancellationToken), _albumParser);
        if (!response.IsSuccess || response.Value is null) return SearchAttempt.Failure(response.Error);
        var items = QqSearchMapper.Albums(response.Value);
        var total = Math.Max(response.Value.Page.TotalCount ?? items.Count, items.Count);
        return SearchAttempt.Success(new(items, total, HasMore(offset, items.Count, total), false));
    }

    private async Task<SearchAttempt> SearchArtistsAsync(
        string keyword, int page, int pageSize, int offset, CancellationToken cancellationToken)
    {
        if (page > 1)
            return SearchAttempt.Success(new([], 0, false, false));
        var key = QqSearchRequestKeys.Search(SearchResultFilter.Artists, keyword, page, pageSize);
        var response = await _httpClient.SendAsync(
            () => CreateSmartboxRequest(keyword), Context("artists", key, cancellationToken), _artistParser);
        if (!response.IsSuccess || response.Value is null) return SearchAttempt.Failure(response.Error);
        var allItems = QqSearchMapper.Artists(response.Value);
        var items = allItems.Skip(offset).Take(pageSize).ToArray();
        var total = Math.Max(response.Value.Group.Count ?? allItems.Count, allItems.Count);
        return SearchAttempt.Success(new(items, total, HasMore(offset, items.Length, total), false));
    }

    private static HttpRequestMessage CreateClientSearchRequest(string keyword, int page, int pageSize, int type)
    {
        var query = $"format=json&w={Uri.EscapeDataString(keyword)}&n={pageSize}&p={page}&t={type}";
        var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(ClientSearchEndpoint) { Query = query }.Uri);
        request.Headers.Referrer = PublicReferer;
        return request;
    }

    private static HttpRequestMessage CreateSmartboxRequest(string keyword)
    {
        var query = $"format=json&s_from=pc_header&type=1&key={Uri.EscapeDataString(keyword)}";
        var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(SmartboxEndpoint) { Query = query }.Uri);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        return request;
    }

    private static PlatformParseResult<QqTrackSearchPayload> ParseTracks(QqClientSearchEnvelope envelope)
    {
        if (envelope.Code is null)
            return PlatformParseResult<QqTrackSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌曲搜索缺少业务状态");
        if (envelope.Code != 0)
            return PlatformParseResult<QqTrackSearchPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐歌曲搜索业务请求失败");
        var page = envelope.Data?.Song;
        if (page?.Items is null || page.TotalCount is null)
            return PlatformParseResult<QqTrackSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌曲搜索缺少数据节点");
        if (page.Items.Any(item => string.IsNullOrWhiteSpace(item.SongMid) || string.IsNullOrWhiteSpace(item.SongName ?? item.Name)))
            return PlatformParseResult<QqTrackSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌曲搜索缺少必需字段");
        return PlatformParseResult<QqTrackSearchPayload>.Success(new(page));
    }

    private static PlatformParseResult<QqAlbumSearchPayload> ParseAlbums(QqClientSearchEnvelope envelope)
    {
        if (envelope.Code is null)
            return PlatformParseResult<QqAlbumSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐专辑搜索缺少业务状态");
        if (envelope.Code != 0)
            return PlatformParseResult<QqAlbumSearchPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐专辑搜索业务请求失败");
        var page = envelope.Data?.Album;
        if (page?.Items is null || page.TotalCount is null)
            return PlatformParseResult<QqAlbumSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐专辑搜索缺少数据节点");
        if (page.Items.Any(item =>
                string.IsNullOrWhiteSpace(item.AlbumMid ?? item.Mid) ||
                string.IsNullOrWhiteSpace(item.AlbumName ?? item.Name)))
            return PlatformParseResult<QqAlbumSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐专辑搜索缺少必需字段");
        return PlatformParseResult<QqAlbumSearchPayload>.Success(new(page));
    }

    private static PlatformParseResult<QqArtistSearchPayload> ParseArtists(QqSmartboxEnvelope envelope)
    {
        if (envelope.Code is null)
            return PlatformParseResult<QqArtistSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌手搜索缺少业务状态");
        if (envelope.Code != 0)
            return PlatformParseResult<QqArtistSearchPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐歌手搜索业务请求失败");
        var group = envelope.Data?.Singers;
        if (group?.Items is null)
            return PlatformParseResult<QqArtistSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌手搜索缺少数据节点");
        if (group.Items.Any(item => string.IsNullOrWhiteSpace(item.Mid) || string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<QqArtistSearchPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐歌手搜索缺少必需字段");
        return PlatformParseResult<QqArtistSearchPayload>.Success(new(group));
    }

    private static PlatformParseResult<QqSuggestionPayload> ParseSuggestions(QqSmartboxEnvelope envelope)
    {
        if (envelope.Code is null)
            return PlatformParseResult<QqSuggestionPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐搜索建议缺少业务状态");
        if (envelope.Code != 0)
            return PlatformParseResult<QqSuggestionPayload>.Failure(PlatformErrorCode.ServiceUnavailable, "QQ 音乐搜索建议业务请求失败");
        if (envelope.Data is null)
            return PlatformParseResult<QqSuggestionPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐搜索建议缺少数据节点");
        var items = new[] { envelope.Data.Singers, envelope.Data.Songs, envelope.Data.Albums }
            .Where(group => group is not null)
            .SelectMany(group => group!.Items ?? []);
        if (items.Any(item => string.IsNullOrWhiteSpace(item.Name)))
            return PlatformParseResult<QqSuggestionPayload>.Failure(PlatformErrorCode.InvalidResponse, "QQ 音乐搜索建议缺少必需字段");
        return PlatformParseResult<QqSuggestionPayload>.Success(new(envelope.Data));
    }

    private static PlatformRequestContext Context(string operation, string requestKey, CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery("qq", $"qq.search.{operation}", requestKey, cancellationToken, TimeSpan.FromSeconds(8));

    private bool TryCached(
        DiscoveryCacheKey key, bool includeExpired, out DiscoveryCacheValue<QqSearchCachePayload>? cached) =>
        _cache.TryGetValue(key, includeExpired, out cached);

    private static SearchAdapterResponse FromLive(QqSearchCachePayload payload, DateTimeOffset started) =>
        BuildResponse(payload, SearchDataOrigin.Live, DateTimeOffset.UtcNow - started, DateTimeOffset.UtcNow);

    private static SearchAdapterResponse FromCache(
        DiscoveryCacheValue<QqSearchCachePayload> cached, SearchDataOrigin origin, DateTimeOffset started) =>
        BuildResponse(cached.Value, origin, DateTimeOffset.UtcNow - started, cached.LoadedAt);

    private static SearchAdapterResponse BuildResponse(
        QqSearchCachePayload payload, SearchDataOrigin origin, TimeSpan elapsed, DateTimeOffset loadedAt)
    {
        var items = payload.Items.Select(item => item with { DataOrigin = origin }).ToArray();
        var message = origin switch
        {
            SearchDataOrigin.CacheFresh => "QQ 音乐搜索结果来自缓存",
            SearchDataOrigin.CacheStale => "QQ 音乐网络不可用，正在显示上次搜索结果",
            _ when payload.IsPartialSuccess => "QQ 音乐部分搜索类型暂时不可用",
            _ when items.Length == 0 => "QQ 音乐没有匹配结果",
            _ => "QQ 音乐搜索完成"
        };
        return new(PlatformId.QqMusic, items, payload.TotalCount, payload.HasMore,
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
        return new(PlatformId.QqMusic, [], 0, false, state, SearchDataOrigin.Live,
            state == SearchSourceState.Unauthorized ? "请先登录 QQ 音乐后重试搜索" : "QQ 音乐搜索暂时不可用",
            error?.RequiresLogin == true, DateTimeOffset.UtcNow - started, ErrorCode: error?.Code, LoadedAt: DateTimeOffset.UtcNow);
    }

    private static SearchAdapterResponse Empty(DateTimeOffset started, string message) =>
        new(PlatformId.QqMusic, [], 0, false, SearchSourceState.Empty, SearchDataOrigin.Live,
            message, false, DateTimeOffset.UtcNow - started, LoadedAt: DateTimeOffset.UtcNow);

    private static SearchAdapterResponse Unsupported(DateTimeOffset started, string message) =>
        new(PlatformId.QqMusic, [], 0, false, SearchSourceState.Unsupported, SearchDataOrigin.Live,
            message, false, DateTimeOffset.UtcNow - started, ErrorCode: PlatformErrorCode.Unsupported, LoadedAt: DateTimeOffset.UtcNow);

    private PlatformError PreferredError(IEnumerable<PlatformError?> errors)
    {
        var values = errors.Where(error => error is not null).Cast<PlatformError>().ToArray();
        return values.FirstOrDefault(error => error.Code is PlatformErrorCode.Unauthorized or PlatformErrorCode.CredentialExpired)
               ?? values.FirstOrDefault()
               ?? _errorMapper.FromCode("qq", Guid.NewGuid().ToString("N"), PlatformErrorCode.Unknown);
    }

    private static bool HasMore(int offset, int count, int total) => count > 0 && offset + count < total;

    private static string NormalizeKeyword(string keyword)
    {
        var value = new string(keyword.Trim().Where(character => !char.IsControl(character)).ToArray());
        return value.Length <= 80 ? value : value[..80];
    }

    private sealed record QqSearchCachePayload(
        IReadOnlyList<SearchResultItem> Items,
        int TotalCount,
        bool HasMore,
        bool IsPartialSuccess);

    private sealed record SearchAttempt(bool IsSuccess, QqSearchCachePayload? Payload, PlatformError? Error)
    {
        public static SearchAttempt Success(QqSearchCachePayload payload) => new(true, payload, null);
        public static SearchAttempt Failure(PlatformError? error) => new(false, null, error);
    }
}
