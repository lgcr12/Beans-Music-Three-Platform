using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public sealed record DiscoveryAdapterCapabilities(
    bool SupportsAnonymousHero,
    bool SupportsAnonymousRankings,
    bool SupportsAnonymousRecommendedPlaylists,
    bool SupportsAnonymousPlaylistSquare,
    bool SupportsAnonymousCategories,
    bool SupportsAuthenticatedDailyRecommendations,
    bool SupportsAuthenticatedUserPlaylists = false,
    bool SupportsSearch = false,
    bool SupportsPlayback = false);

public interface IPlatformDiscoveryAdapter
{
    string PlatformId { get; }
    DiscoveryAdapterCapabilities Capabilities { get; }
    Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(PlatformRequestContext context, CancellationToken cancellationToken);
    Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(PlatformRequestContext context, CancellationToken cancellationToken);
    Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicRecommendedPlaylistsAsync(PlatformRequestContext context, CancellationToken cancellationToken);
    Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(string? category, int page, PlatformRequestContext context, CancellationToken cancellationToken);
    Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(PlatformRequestContext context, CancellationToken cancellationToken);
    Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(PlatformRequestContext context, CancellationToken cancellationToken);
}
