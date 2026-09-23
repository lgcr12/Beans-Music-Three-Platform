using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public interface IMusicDiscoveryService
{
    Task<PlatformDiscoveryContent> GetDiscoveryAsync(string platformId, DiscoveryRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<RankingSummary>> GetRankingsAsync(string platformId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicPlaylist>> GetRecommendedPlaylistsAsync(string platformId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(string platformId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MusicPlaylist>> GetPlaylistSquareAsync(string platformId, string? category, int page, CancellationToken cancellationToken);
    void Invalidate(string platformId);
}

public readonly record struct DiscoveryCacheKey(
    string PlatformId,
    string ContentType,
    string Category,
    int Page,
    string AccountIdentityHash)
{
    public override string ToString() => $"{PlatformId}:{ContentType}:{Category}:{Page}:{AccountIdentityHash}";
}

public interface IDiscoveryCache
{
    bool TryGet(DiscoveryCacheKey key, out PlatformDiscoveryContent? content);
    void Set(DiscoveryCacheKey key, PlatformDiscoveryContent content, TimeSpan lifetime);
    bool TryGetValue<T>(DiscoveryCacheKey key, bool includeExpired, out DiscoveryCacheValue<T>? value);
    void SetValue<T>(DiscoveryCacheKey key, T value, TimeSpan lifetime);
    void RemovePlatform(string platformId);
}

public sealed record DiscoveryCacheValue<T>(
    T Value,
    DateTimeOffset LoadedAt,
    DateTimeOffset ExpiresAt,
    bool IsStale);
