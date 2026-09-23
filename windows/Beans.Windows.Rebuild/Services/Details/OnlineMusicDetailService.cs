using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms;

namespace Beans.Windows.Rebuild.Services.Details;

public sealed class OnlineMusicDetailService : IOnlineMusicDetailService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly IReadOnlyDictionary<PlatformId, IOnlineMusicDetailAdapter> _adapters;
    private readonly IDiscoveryCache _cache;

    public OnlineMusicDetailService(IEnumerable<IOnlineMusicDetailAdapter> adapters, IDiscoveryCache cache)
    {
        _adapters = adapters.GroupBy(adapter => adapter.Platform).ToDictionary(group => group.Key, group => group.Single());
        _cache = cache;
    }

    public async Task<OnlineMusicDetailResponse> GetDetailAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedId = query.NativeId?.Trim() ?? string.Empty;
        if (!OnlineMusicDetailValidator.IsValid(query.Platform, query.Kind, normalizedId))
            return OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.InvalidRequest, "平台详情标识无效", PlatformErrorCode.InvalidRequest);

        if (!_adapters.TryGetValue(query.Platform, out var adapter) || !adapter.Supports(query.Kind))
            return OnlineMusicDetailResponse.Failure(OnlineMusicDetailState.Unsupported, $"{query.Platform.ToDisplayName()}当前不支持此详情类型", PlatformErrorCode.Unsupported);

        var normalized = query with { NativeId = normalizedId };
        var cacheKey = StorageKey(normalized);
        if (!query.ForceRefresh && _cache.TryGetValue<OnlineMusicDetailContent>(cacheKey, false, out var fresh) && fresh is not null)
            return OnlineMusicDetailResponse.Success(WithOrigin(fresh.Value, SearchDataOrigin.CacheFresh, fresh.LoadedAt), "正在显示缓存详情");

        var response = await adapter.GetDetailAsync(normalized, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccess && response.Content is not null)
        {
            var live = WithOrigin(response.Content, SearchDataOrigin.Live, response.Content.LoadedAt);
            _cache.SetValue(cacheKey, live, CacheLifetime);
            return response with { Content = live };
        }

        if (_cache.TryGetValue<OnlineMusicDetailContent>(cacheKey, true, out var stale) && stale is not null)
            return OnlineMusicDetailResponse.Success(WithOrigin(stale.Value, SearchDataOrigin.CacheStale, stale.LoadedAt), "网络暂时不可用，正在显示上次详情");

        return response;
    }

    public void Invalidate(PlatformId platform, OnlineMusicDetailKind kind, string nativeId)
    {
        // The shared bounded cache only exposes platform invalidation. Avoid retaining a stale
        // platform detail after an explicit refresh request.
        _cache.RemovePlatform(platform.ToStableId());
    }

    private static DiscoveryCacheKey StorageKey(OnlineMusicDetailQuery query) =>
        new(query.Platform.ToStableId(), $"detail-{query.Kind.ToString().ToLowerInvariant()}", query.NativeId, 1, AccountIdentityHasher.Anonymous);

    private static OnlineMusicDetailContent WithOrigin(OnlineMusicDetailContent content, SearchDataOrigin origin, DateTimeOffset loadedAt) =>
        content with
        {
            DataOrigin = origin,
            LoadedAt = loadedAt,
            Tracks = content.Tracks.Select(track => track with { DataOrigin = origin }).ToArray()
        };
}

public static class OnlineMusicDetailValidator
{
    public static bool IsValid(PlatformId platform, OnlineMusicDetailKind kind, string? nativeId)
    {
        var value = nativeId?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 128 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || value.IndexOfAny(['/', '\\', ':', '?', '&', '#']) >= 0)
            return false;

        return platform switch
        {
            PlatformId.NetEaseMusic => ulong.TryParse(value, out var numeric) && numeric > 0,
            PlatformId.QqMusic when kind is OnlineMusicDetailKind.Playlist or OnlineMusicDetailKind.Ranking =>
                ulong.TryParse(value, out var numeric) && numeric > 0,
            PlatformId.QqMusic => value.All(char.IsLetterOrDigit),
            _ => false
        };
    }
}
