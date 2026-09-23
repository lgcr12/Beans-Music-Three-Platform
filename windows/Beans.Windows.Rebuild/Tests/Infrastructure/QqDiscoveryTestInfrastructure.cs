using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;

namespace Beans.Windows.Rebuild.Tests.Infrastructure;

internal static class QqDiscoveryTestInfrastructure
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "QQ", name));

    public static PlatformHttpClientFactory Factory(
        HttpMessageHandler qqHandler,
        CaptureSafeLogger? logger = null) =>
        new(
            new Dictionary<string, HttpMessageHandler>
            {
                ["qq"] = qqHandler,
                ["netease"] = SuccessHandler(),
                ["kugou"] = SuccessHandler()
            },
            logger ?? new CaptureSafeLogger(),
            new SensitiveDataRedactor(),
            new PlatformErrorMapper());

    public static PlatformRequestContext Context(
        string operation,
        string requestKey,
        CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery("qq", operation, requestKey, cancellationToken);

    private static HttpMessageHandler SuccessHandler() => new FakeHttpMessageHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        }));
}

internal sealed class ScriptedQqDiscoveryAdapter : IPlatformDiscoveryAdapter
{
    public string PlatformId => "qq";
    public DiscoveryAdapterCapabilities Capabilities { get; } = new(true, true, true, false, false, false);
    public int RankingCallCount { get; private set; }
    public int RecommendedCallCount { get; private set; }

    public Func<PlatformRequestContext, CancellationToken, Task<PlatformResponse<IReadOnlyList<RankingSummary>>>> Rankings { get; set; } =
        (context, _) => Task.FromResult(Success<IReadOnlyList<RankingSummary>>(
            [new RankingSummary("26", "Live chart", "Updated", "ms-appx:///Assets/Branding/beans-icon.png", ["Track"], "qq", "QQ 音乐")], context));

    public Func<PlatformRequestContext, CancellationToken, Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>>> Recommended { get; set; } =
        (context, _) => Task.FromResult(Success<IReadOnlyList<MusicPlaylist>>(
            [new MusicPlaylist(new MusicIdentity(Beans.Windows.Rebuild.Models.PlatformId.QqMusic, "880001"), "Live playlist", "QQ Music", new Uri("ms-appx:///Assets/Branding/beans-icon.png"), 20)], context));

    public Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(PlatformRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Success(new DiscoveryHero("Live", "Public", "ms-appx:///Assets/Branding/beans-icon.png", "Open", "880001"), context));

    public Task<PlatformResponse<IReadOnlyList<RankingSummary>>> GetPublicRankingsAsync(PlatformRequestContext context, CancellationToken cancellationToken)
    {
        RankingCallCount++;
        return Rankings(context, cancellationToken);
    }

    public Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicRecommendedPlaylistsAsync(PlatformRequestContext context, CancellationToken cancellationToken)
    {
        RecommendedCallCount++;
        return Recommended(context, cancellationToken);
    }

    public Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(string? category, int page, PlatformRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Failure<IReadOnlyList<MusicPlaylist>>(context, PlatformErrorCode.Unsupported, "Unsupported"));

    public Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(PlatformRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Failure<IReadOnlyList<MusicTrack>>(context, PlatformErrorCode.Unsupported, "Requires authorization"));

    public Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(PlatformRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Failure<IReadOnlyList<string>>(context, PlatformErrorCode.Unsupported, "Unsupported"));

    public static PlatformResponse<T> Success<T>(T value, PlatformRequestContext context) =>
        PlatformResponse<T>.Success(value, context.CorrelationId, TimeSpan.Zero, cacheKey: context.RequestKey);

    public static PlatformResponse<T> Failure<T>(
        PlatformRequestContext context,
        PlatformErrorCode code = PlatformErrorCode.NetworkUnavailable,
        string message = "Network unavailable")
    {
        var error = new PlatformError("qq", code, message, code == PlatformErrorCode.NetworkUnavailable,
            code == PlatformErrorCode.NetworkUnavailable, false, context.CorrelationId);
        return PlatformResponse<T>.Failure(error, TimeSpan.Zero, cacheKey: context.RequestKey);
    }
}
