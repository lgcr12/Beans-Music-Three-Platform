using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;

namespace Beans.Windows.Rebuild.Services.Platforms;

public sealed class StubPlatformDiscoveryAdapter : IPlatformDiscoveryAdapter
{
    private readonly IPlatformErrorMapper _errorMapper;

    public StubPlatformDiscoveryAdapter(string platformId, DiscoveryAdapterCapabilities capabilities, IPlatformErrorMapper errorMapper)
    {
        PlatformId = platformId;
        Capabilities = capabilities;
        _errorMapper = errorMapper;
    }

    public string PlatformId { get; }
    public DiscoveryAdapterCapabilities Capabilities { get; }

    public Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Unsupported<DiscoveryHero>(context);
    public Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<RankingSummary>>(context);
    public Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicRecommendedPlaylistsAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<MusicPlaylist>>(context);
    public Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(string? category, int page, PlatformRequestContext context, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<MusicPlaylist>>(context);
    public Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<MusicTrack>>(context);
    public Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(PlatformRequestContext context, CancellationToken cancellationToken) => Unsupported<IReadOnlyList<string>>(context);

    private Task<PlatformResponse<T>> Unsupported<T>(PlatformRequestContext context)
    {
        var error = _errorMapper.FromCode(PlatformId, context.CorrelationId, PlatformErrorCode.Unsupported,
            "在线服务尚未连接");
        return Task.FromResult(PlatformResponse<T>.Failure(error, TimeSpan.Zero));
    }
}
