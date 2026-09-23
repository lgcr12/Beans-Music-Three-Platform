using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.PreviewData;

namespace Beans.Windows.Rebuild.Services.Platforms;

public sealed class PreviewDiscoveryService(
    IDiscoveryCache cache,
    IMusicPlatformRegistry registry,
    IPreviewModePolicy previewMode) : IMusicDiscoveryService
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(20);

    public async Task<PlatformDiscoveryContent> GetDiscoveryAsync(string platformId, DiscoveryRequest request, CancellationToken cancellationToken)
    {
        if (!previewMode.IsEnabled) throw new PreviewContentUnavailableException();
        var descriptor = registry.GetPlatform(platformId) ?? throw new InvalidOperationException("未知平台");
        if (!descriptor.IsEnabled || !descriptor.IsAvailable) throw new InvalidOperationException("该平台当前不可用");

        var category = request.Category ?? "推荐";
        var accountKey = descriptor.AuthorizationState == AuthorizationState.Authorized ? "authorized" : "anonymous";
        var key = new DiscoveryCacheKey(platformId, "discovery", category, request.Page, accountKey);
        if (!request.ForceRefresh && cache.TryGet(key, out var cached) && cached is not null) return cached;

        await Task.Delay(140, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var content = platformId switch
        {
            "qq" => QqDiscoveryPreviewData.Create(category),
            "netease" => NetEaseDiscoveryPreviewData.Create(category),
            "kugou" => KuGouDiscoveryPreviewData.Create(category),
            _ => throw new InvalidOperationException("该平台没有发现页")
        };
        cache.Set(key, content, PreviewLifetime);
        return content;
    }

    public async Task<IReadOnlyList<RankingSummary>> GetRankingsAsync(string platformId, CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).Rankings;

    public async Task<IReadOnlyList<MusicPlaylist>> GetRecommendedPlaylistsAsync(string platformId, CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).RecommendedPlaylists;

    public async Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(string platformId, CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).DailyRecommendations;

    public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistSquareAsync(string platformId, string? category, int page, CancellationToken cancellationToken) =>
        (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId, category, page), cancellationToken)).PlaylistSquare;

    public void Invalidate(string platformId) => cache.RemovePlatform(platformId);
}
