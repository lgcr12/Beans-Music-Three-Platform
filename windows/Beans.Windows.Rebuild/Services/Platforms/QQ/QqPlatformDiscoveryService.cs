using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

public sealed class QqPlatformDiscoveryService : IMusicDiscoveryService
{
    private static readonly TimeSpan HeroLifetime = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RankingsLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RecommendedLifetime = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan CategoriesLifetime = TimeSpan.FromHours(18);
    private static readonly TimeSpan PlaylistSquareLifetime = TimeSpan.FromMinutes(20);

    private readonly IReadOnlyDictionary<string, IPlatformDiscoveryAdapter> _adapters;
    private readonly IDiscoveryCache _cache;
    private readonly IMusicPlatformRegistry _registry;
    private readonly PreviewDiscoveryService _previewService;
    private readonly IPreviewModePolicy _previewMode;

    public QqPlatformDiscoveryService(
        IEnumerable<IPlatformDiscoveryAdapter> adapters,
        IDiscoveryCache cache,
        IMusicPlatformRegistry registry,
        PreviewDiscoveryService previewService,
        IPreviewModePolicy previewMode)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.PlatformId, StringComparer.Ordinal);
        _cache = cache;
        _registry = registry;
        _previewService = previewService;
        _previewMode = previewMode;
    }

    public async Task<PlatformDiscoveryContent> GetDiscoveryAsync(
        string platformId,
        DiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(platformId, out var adapter) ||
            (!string.Equals(platformId, "qq", StringComparison.Ordinal) &&
             !string.Equals(platformId, "netease", StringComparison.Ordinal)))
            return await _previewService.GetDiscoveryAsync(platformId, request, cancellationToken);

        var descriptor = _registry.GetPlatform(platformId) ?? throw new InvalidOperationException("未知平台");
        if (!descriptor.IsEnabled || !descriptor.IsAvailable) throw new InvalidOperationException("该平台当前不可用");

        var keys = RequestKeys(platformId, request.Category, request.Page);
        var rankingsTask = LoadAsync(
            platformId, "rankings", string.Empty, 1, keys.Rankings, RankingsLifetime, request.ForceRefresh,
            (context, token) => adapter.GetPublicRankingsAsync(context, token), cancellationToken);
        var recommendedTask = LoadAsync(
            platformId, "recommended-playlists", string.Empty, 1, keys.Recommended, RecommendedLifetime,
            request.ForceRefresh,
            (context, token) => adapter.GetPublicRecommendedPlaylistsAsync(context, token), cancellationToken);
        var categoriesTask = LoadAsync(
            platformId, "categories", string.Empty, 1, keys.Categories, CategoriesLifetime, request.ForceRefresh,
            (context, token) => adapter.GetCategoriesAsync(context, token), cancellationToken);
        var squareTask = LoadAsync(
            platformId, "playlist-square", NormalizeCategory(request.Category), request.Page, keys.Square,
            PlaylistSquareLifetime, request.ForceRefresh,
            (context, token) => adapter.GetPublicPlaylistSquareAsync(request.Category, request.Page, context, token),
            cancellationToken);

        await Task.WhenAll(rankingsTask, recommendedTask, categoriesTask, squareTask);
        cancellationToken.ThrowIfCancellationRequested();
        var rankings = await rankingsTask;
        var recommended = await recommendedTask;
        var categories = await categoriesTask;
        var square = await squareTask;
        var hero = LoadHero(platformId, keys.Hero, rankings, recommended, request.ForceRefresh, cancellationToken);

        var dailyContext = Context(platformId, "daily-recommendations",
            $"{platformId}:discovery:daily-recommendations:anonymous", cancellationToken);
        var daily = await adapter.GetDailyRecommendationsAsync(dailyContext, cancellationToken);

        var sections = new List<DiscoverySectionState>
        {
            Section("hero", hero),
            Section("rankings", rankings),
            Section("recommended-playlists", recommended),
            Section("categories", categories),
            Section("playlist-square", square)
        };
        sections.Add(Section("daily-recommendations", daily));

        if (sections.All(section => !section.IsSuccess))
        {
            if (_previewMode.IsEnabled)
                return await _previewService.GetDiscoveryAsync(platformId, request, cancellationToken);
            var message = sections.Select(section => section.SafeMessage).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? $"{DisplayName(platformId)}公开内容暂时无法加载";
            throw new PlatformDiscoveryUnavailableException(message);
        }

        var origin = AggregateOrigin(sections.Where(section => section.IsSuccess));
        var loadedAt = sections.Where(section => section.IsSuccess)
            .Select(section => section.LoadedAt)
            .DefaultIfEmpty(DateTimeOffset.UtcNow)
            .Max();
        var isPartial = sections.Any(section => !section.IsSuccess);
        var platformName = DisplayName(platformId);

        return new PlatformDiscoveryContent(
            platformId,
            hero.Value ?? EmptyHero(platformId),
            daily.Value ?? [],
            rankings.Value ?? [],
            recommended.Value ?? [],
            square.Value ?? [],
            categories.Value ?? [],
            false,
            isPartial,
            loadedAt,
            isPartial ? $"{platformName}公开内容已加载，部分板块暂不可用" : $"{platformName}公开内容已更新",
            origin,
            sections.Any(section => section.IsStale),
            false,
            StatusText(origin),
            sections);
    }

    public async Task<IReadOnlyList<RankingSummary>> GetRankingsAsync(string platformId, CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).Rankings;

    public async Task<IReadOnlyList<MusicPlaylist>> GetRecommendedPlaylistsAsync(string platformId, CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).RecommendedPlaylists;

    public async Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(string platformId, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(platformId, out var adapter))
            return await _previewService.GetDailyRecommendationsAsync(platformId, cancellationToken);
        var context = Context(platformId, "daily-recommendations",
            $"{platformId}:discovery:daily-recommendations:anonymous", cancellationToken);
        var response = await adapter.GetDailyRecommendationsAsync(context, cancellationToken);
        return response.Value ?? [];
    }

    public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistSquareAsync(
        string platformId,
        string? category,
        int page,
        CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId, category, page), cancellationToken)).PlaylistSquare;

    public void Invalidate(string platformId) => _cache.RemovePlatform(platformId);

    private async Task<PlatformResponse<T>> LoadAsync<T>(
        string platformId,
        string operation,
        string category,
        int page,
        string requestKey,
        TimeSpan lifetime,
        bool forceRefresh,
        Func<PlatformRequestContext, CancellationToken, Task<PlatformResponse<T>>> loader,
        CancellationToken cancellationToken)
    {
        var cacheKey = new DiscoveryCacheKey(platformId, operation, category, page, AccountIdentityHasher.Anonymous);
        var context = Context(platformId, operation, requestKey, cancellationToken);
        if (!forceRefresh && _cache.TryGetValue<T>(cacheKey, false, out var fresh) && fresh is not null)
            return Cached(fresh, context, cacheKey, DiscoveryDataOrigin.CacheFresh);

        var response = await loader(context, cancellationToken);
        if (response.IsSuccess && response.Value is not null)
        {
            _cache.SetValue(cacheKey, response.Value, lifetime);
            return response with { CacheKey = cacheKey.ToString() };
        }

        if (_cache.TryGetValue<T>(cacheKey, false, out fresh) && fresh is not null)
            return Cached(fresh, context, cacheKey, DiscoveryDataOrigin.CacheFresh);
        if (_cache.TryGetValue<T>(cacheKey, true, out var stale) && stale is not null)
            return Cached(stale, context, cacheKey, DiscoveryDataOrigin.CacheStale);
        return response with { CacheKey = cacheKey.ToString() };
    }

    private PlatformResponse<DiscoveryHero> LoadHero(
        string platformId,
        string requestKey,
        PlatformResponse<IReadOnlyList<RankingSummary>> rankings,
        PlatformResponse<IReadOnlyList<MusicPlaylist>> recommended,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = new DiscoveryCacheKey(platformId, "hero", string.Empty, 1, AccountIdentityHasher.Anonymous);
        var context = Context(platformId, "hero", requestKey, cancellationToken);
        if (!forceRefresh && _cache.TryGetValue<DiscoveryHero>(cacheKey, false, out var fresh) && fresh is not null)
            return Cached(fresh, context, cacheKey, DiscoveryDataOrigin.CacheFresh);

        DiscoveryHero? value = null;
        var source = DiscoveryDataOrigin.Live;
        if (recommended is { IsSuccess: true, Value.Count: > 0 })
        {
            value = string.Equals(platformId, "netease", StringComparison.Ordinal)
                ? NetEaseDiscoveryMapper.Hero(recommended.Value)
                : QqDiscoveryMapper.Hero(recommended.Value);
            source = recommended.DataOrigin;
        }
        else if (rankings is { IsSuccess: true, Value.Count: > 0 })
        {
            value = string.Equals(platformId, "netease", StringComparison.Ordinal)
                ? NetEaseDiscoveryMapper.Hero(rankings.Value)
                : QqDiscoveryMapper.Hero(rankings.Value);
            source = rankings.DataOrigin;
        }

        if (value is not null)
        {
            if (source == DiscoveryDataOrigin.Live) _cache.SetValue(cacheKey, value, HeroLifetime);
            return PlatformResponse<DiscoveryHero>.Success(
                value, context.CorrelationId, TimeSpan.Zero, origin: source, cacheKey: cacheKey.ToString(),
                isStale: source == DiscoveryDataOrigin.CacheStale, safeMessage: StatusText(source));
        }

        if (_cache.TryGetValue<DiscoveryHero>(cacheKey, false, out fresh) && fresh is not null)
            return Cached(fresh, context, cacheKey, DiscoveryDataOrigin.CacheFresh);
        if (_cache.TryGetValue<DiscoveryHero>(cacheKey, true, out var stale) && stale is not null)
            return Cached(stale, context, cacheKey, DiscoveryDataOrigin.CacheStale);

        var error = recommended.Error ?? rankings.Error ?? new PlatformError(
            platformId, PlatformErrorCode.InvalidResponse, $"{DisplayName(platformId)}公开内容为空",
            false, false, false, context.CorrelationId);
        return PlatformResponse<DiscoveryHero>.Failure(error, TimeSpan.Zero, cacheKey: cacheKey.ToString());
    }

    private static PlatformResponse<T> Cached<T>(
        DiscoveryCacheValue<T> cached,
        PlatformRequestContext context,
        DiscoveryCacheKey cacheKey,
        DiscoveryDataOrigin origin) =>
        PlatformResponse<T>.Success(
            cached.Value, context.CorrelationId, TimeSpan.Zero, origin: origin, statusCode: null,
            cacheKey: cacheKey.ToString(), isStale: origin == DiscoveryDataOrigin.CacheStale,
            safeMessage: StatusText(origin)) with
        {
            LoadedAt = cached.LoadedAt
        };

    private static PlatformRequestContext Context(
        string platformId,
        string operation,
        string requestKey,
        CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery(platformId, $"{platformId}.discovery.{operation}", requestKey, cancellationToken);

    private static DiscoverySectionState Section<T>(string id, PlatformResponse<T> response) => new(
        id, response.IsSuccess, response.Error?.Code, response.SafeMessage, response.DataOrigin,
        response.LoadedAt, response.IsStale);

    private static DiscoveryDataOrigin AggregateOrigin(IEnumerable<DiscoverySectionState> states)
    {
        var origins = states.Select(state => state.DataOrigin).ToArray();
        if (origins.Contains(DiscoveryDataOrigin.CacheStale)) return DiscoveryDataOrigin.CacheStale;
        if (origins.Contains(DiscoveryDataOrigin.Live)) return DiscoveryDataOrigin.Live;
        if (origins.Contains(DiscoveryDataOrigin.CacheFresh)) return DiscoveryDataOrigin.CacheFresh;
        return DiscoveryDataOrigin.Preview;
    }

    private static string StatusText(DiscoveryDataOrigin origin) => origin switch
    {
        DiscoveryDataOrigin.Live => "公开内容",
        DiscoveryDataOrigin.CacheFresh => "来自缓存",
        DiscoveryDataOrigin.CacheStale => "网络不可用，正在显示上次内容",
        _ => "预览内容 · 在线服务尚未连接"
    };

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "all" : category.Trim();

    private static string DisplayName(string platformId) => platformId switch
    {
        "netease" => "网易云音乐",
        "qq" => "QQ 音乐",
        _ => "音乐平台"
    };

    private static DiscoveryHero EmptyHero(string platformId) => new(
        $"{DisplayName(platformId)}公开内容",
        "当前公开内容暂为空",
        "ms-appx:///Assets/Branding/beans-icon.png",
        "查看公开内容");

    private static DiscoveryRequestKeys RequestKeys(string platformId, string? category, int page) =>
        string.Equals(platformId, "netease", StringComparison.Ordinal)
            ? new(NetEaseDiscoveryRequestKeys.Hero, NetEaseDiscoveryRequestKeys.Rankings,
                NetEaseDiscoveryRequestKeys.RecommendedPlaylists, NetEaseDiscoveryRequestKeys.Categories,
                NetEaseDiscoveryRequestKeys.PlaylistSquare(category, page))
            : new(QqDiscoveryRequestKeys.Hero, QqDiscoveryRequestKeys.Rankings,
                QqDiscoveryRequestKeys.RecommendedPlaylists, QqDiscoveryRequestKeys.Categories,
                QqDiscoveryRequestKeys.PlaylistSquare(category, page));

    private sealed record DiscoveryRequestKeys(
        string Hero,
        string Rankings,
        string Recommended,
        string Categories,
        string Square);
}

public sealed class PlatformDiscoveryUnavailableException(string safeMessage) : InvalidOperationException(safeMessage);
