using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;

namespace Beans.Windows.Rebuild.Tests.Infrastructure;

internal static class NetEaseDiscoveryTestInfrastructure
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetEase", name));

    public static PlatformHttpClientFactory Factory(
        HttpMessageHandler netEaseHandler,
        CaptureSafeLogger? logger = null) =>
        new(
            new Dictionary<string, HttpMessageHandler>
            {
                ["qq"] = SuccessHandler(),
                ["netease"] = netEaseHandler,
                ["kugou"] = SuccessHandler()
            },
            logger ?? new CaptureSafeLogger(),
            new SensitiveDataRedactor(),
            new PlatformErrorMapper());

    public static PlatformRequestContext Context(
        string operation,
        string requestKey,
        CancellationToken cancellationToken) =>
        PlatformRequestContext.PublicDiscovery("netease", operation, requestKey, cancellationToken);

    private static HttpMessageHandler SuccessHandler() => new FakeHttpMessageHandler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        }));
}

internal sealed class ScriptedNetEaseDiscoveryAdapter : IPlatformDiscoveryAdapter
{
    public string PlatformId => "netease";
    public DiscoveryAdapterCapabilities Capabilities { get; } = new(true, true, true, true, true, true);
    public int RankingCallCount { get; private set; }
    public int RecommendedCallCount { get; private set; }
    public int CategoryCallCount { get; private set; }
    public int SquareCallCount { get; private set; }

    public Func<PlatformRequestContext, CancellationToken, Task<PlatformResponse<IReadOnlyList<RankingSummary>>>> Rankings { get; set; } =
        (context, _) => Task.FromResult(Success<IReadOnlyList<RankingSummary>>(
            [new RankingSummary("19723756", "Live NetEase chart", "Updated", "ms-appx:///Assets/Branding/beans-icon.png", ["Track"], "netease", "网易云音乐")], context));

    public Func<PlatformRequestContext, CancellationToken, Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>>> Recommended { get; set; } =
        (context, _) => Task.FromResult(Success<IReadOnlyList<MusicPlaylist>>(
            [new MusicPlaylist(new MusicIdentity(Beans.Windows.Rebuild.Models.PlatformId.NetEaseMusic, "910001"), "Live NetEase playlist", "NetEase", new Uri("ms-appx:///Assets/Branding/beans-icon.png"), 20)], context));

    public Func<string?, int, PlatformRequestContext, CancellationToken, Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>>> Square { get; set; } =
        (category, page, context, _) => Task.FromResult(Success<IReadOnlyList<MusicPlaylist>>(
            [new MusicPlaylist(new MusicIdentity(Beans.Windows.Rebuild.Models.PlatformId.NetEaseMusic, $"{category ?? "all"}-{page}"), "Square", "NetEase", null, null, Category: category)], context));

    public Func<PlatformRequestContext, CancellationToken, Task<PlatformResponse<IReadOnlyList<string>>>> Categories { get; set; } =
        (context, _) => Task.FromResult(Success<IReadOnlyList<string>>(["华语", "摇滚"], context));

    public Task<PlatformResponse<DiscoveryHero>> GetHeroAsync(PlatformRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Success(new DiscoveryHero("Live", "Public", "ms-appx:///Assets/Branding/beans-icon.png", "Open", "910001"), context));

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

    public Task<PlatformResponse<IReadOnlyList<MusicPlaylist>>> GetPublicPlaylistSquareAsync(string? category, int page, PlatformRequestContext context, CancellationToken cancellationToken)
    {
        SquareCallCount++;
        return Square(category, page, context, cancellationToken);
    }

    public Task<PlatformResponse<IReadOnlyList<string>>> GetCategoriesAsync(PlatformRequestContext context, CancellationToken cancellationToken)
    {
        CategoryCallCount++;
        return Categories(context, cancellationToken);
    }

    public Task<PlatformResponse<IReadOnlyList<MusicTrack>>> GetDailyRecommendationsAsync(PlatformRequestContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Failure<IReadOnlyList<MusicTrack>>(context, PlatformErrorCode.Unauthorized, "登录网易云音乐后查看每日推荐"));

    public static PlatformResponse<T> Success<T>(T value, PlatformRequestContext context) =>
        PlatformResponse<T>.Success(value, context.CorrelationId, TimeSpan.Zero, cacheKey: context.RequestKey);

    public static PlatformResponse<T> Failure<T>(
        PlatformRequestContext context,
        PlatformErrorCode code = PlatformErrorCode.NetworkUnavailable,
        string message = "Network unavailable")
    {
        var error = new PlatformError("netease", code, message, code == PlatformErrorCode.NetworkUnavailable,
            code == PlatformErrorCode.NetworkUnavailable, code == PlatformErrorCode.Unauthorized, context.CorrelationId);
        return PlatformResponse<T>.Failure(error, TimeSpan.Zero, cacheKey: context.RequestKey);
    }
}
