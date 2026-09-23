using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;

namespace Beans.Windows.Rebuild.Tests.Infrastructure;

internal class FixtureDiscoveryAdapter(PlatformDiscoveryContent content) : IPlatformDiscoveryAdapter
{
    public string PlatformId => content.PlatformId;
    public DiscoveryAdapterCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

    public virtual Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Result(content.HeroContent, context);
    public virtual Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Result(content.Rankings, context);
    public virtual Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicRecommendedPlaylistsAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Result(content.RecommendedPlaylists, context);
    public virtual Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(string? category, int page, PlatformRequestContext context, CancellationToken cancellationToken) => Result(content.PlaylistSquare, context);
    public virtual Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Result(content.DailyRecommendations, context);
    public virtual Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Result(content.Categories, context);

    protected static Task<PlatformResponse<T>> Result<T>(T value, PlatformRequestContext context) => Task.FromResult(
        PlatformResponse<T>.Success(value, context.CorrelationId, TimeSpan.Zero, origin: DiscoveryDataOrigin.Live, cacheKey: context.RequestKey));
}

internal sealed class FakeDiscoveryAdapter(PlatformDiscoveryContent content) : FixtureDiscoveryAdapter(content)
{
    public int RankingCallCount { get; private set; }

    public override Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(PlatformRequestContext context, CancellationToken cancellationToken)
    {
        RankingCallCount++;
        return base.GetPublicRankingsAsync(context, cancellationToken);
    }
}
