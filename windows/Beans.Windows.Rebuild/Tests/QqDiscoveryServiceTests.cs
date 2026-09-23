using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqDiscoveryServiceTests
{
    [Fact]
    public async Task LiveQqSectionsAreAggregatedWithoutPreviewSubstitution()
    {
        var adapter = new ScriptedQqDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), previewEnabled: false);

        var content = await service.GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.Live, content.DataOrigin);
        Assert.False(content.IsPreview);
        Assert.Single(content.Rankings);
        Assert.Single(content.RecommendedPlaylists);
        Assert.Equal("880001", content.HeroContent.TrackId);
        Assert.True(content.IsPartialSuccess);
        Assert.Contains(content.SectionStates!, section => section.SectionId == "categories" && section.ErrorCode == PlatformErrorCode.Unsupported);
        Assert.Contains(content.SectionStates!, section => section.SectionId == "playlist-square" && section.ErrorCode == PlatformErrorCode.Unsupported);
        Assert.Contains(content.SectionStates!, section => section.SectionId == "daily-recommendations" && section.ErrorCode == PlatformErrorCode.Unsupported);
        Assert.Equal(1, adapter.RankingCallCount);
        Assert.Equal(1, adapter.RecommendedCallCount);
    }

    [Fact]
    public async Task ServiceCreatesExpectedOperationNamesAndSafeRequestKeys()
    {
        string? rankingOperation = null;
        string? rankingKey = null;
        string? recommendedOperation = null;
        string? recommendedKey = null;
        var adapter = new ScriptedQqDiscoveryAdapter();
        adapter.Rankings = (context, _) =>
        {
            rankingOperation = context.OperationName;
            rankingKey = context.RequestKey;
            return Task.FromResult(ScriptedQqDiscoveryAdapter.Success<IReadOnlyList<RankingSummary>>([], context));
        };
        adapter.Recommended = (context, _) =>
        {
            recommendedOperation = context.OperationName;
            recommendedKey = context.RequestKey;
            return Task.FromResult(ScriptedQqDiscoveryAdapter.Success<IReadOnlyList<MusicPlaylist>>([], context));
        };

        await Service(adapter, new DiscoveryCache(), false)
            .GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        Assert.Equal("qq.discovery.rankings", rankingOperation);
        Assert.Equal(QqDiscoveryRequestKeys.Rankings, rankingKey);
        Assert.Equal("qq.discovery.recommended-playlists", recommendedOperation);
        Assert.Equal(QqDiscoveryRequestKeys.RecommendedPlaylists, recommendedKey);
    }

    [Fact]
    public async Task SecondLoadUsesFreshSectionCacheWithoutNetwork()
    {
        var adapter = new ScriptedQqDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);
        await service.GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        var cached = await service.GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.CacheFresh, cached.DataOrigin);
        Assert.Equal("来自缓存", cached.SafeStatusText);
        Assert.Equal(1, adapter.RankingCallCount);
        Assert.Equal(1, adapter.RecommendedCallCount);
    }

    [Fact]
    public async Task ForceRefreshBypassesFreshCacheAndReplacesIt()
    {
        var adapter = new ScriptedQqDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);
        await service.GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);
        adapter.Recommended = (context, _) => Task.FromResult(ScriptedQqDiscoveryAdapter.Success<IReadOnlyList<MusicPlaylist>>(
            [new MusicPlaylist(new MusicIdentity(PlatformId.QqMusic, "880099"), "Refreshed", "QQ Music", new Uri("ms-appx:///Assets/Branding/beans-icon.png"), null)], context));

        var refreshed = await service.GetDiscoveryAsync(
            "qq", new DiscoveryRequest("qq", ForceRefresh: true), TestContext.Current.CancellationToken);

        Assert.Equal(2, adapter.RankingCallCount);
        Assert.Equal(2, adapter.RecommendedCallCount);
        Assert.Equal("880099", refreshed.RecommendedPlaylists[0].Identity.NativeId);
        Assert.Equal(DiscoveryDataOrigin.Live, refreshed.DataOrigin);
    }

    [Fact]
    public async Task FailedForceRefreshKeepsFreshCache()
    {
        var adapter = new ScriptedQqDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);
        var first = await service.GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);
        adapter.Rankings = (context, _) => Task.FromResult(ScriptedQqDiscoveryAdapter.Failure<IReadOnlyList<RankingSummary>>(context));
        adapter.Recommended = (context, _) => Task.FromResult(ScriptedQqDiscoveryAdapter.Failure<IReadOnlyList<MusicPlaylist>>(context));

        var fallback = await service.GetDiscoveryAsync(
            "qq", new DiscoveryRequest("qq", ForceRefresh: true), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.CacheFresh, fallback.DataOrigin);
        Assert.Equal(first.Rankings[0].Id, fallback.Rankings[0].Id);
        Assert.False(fallback.IsStale);
    }

    [Fact]
    public async Task NetworkFailureUsesExpiredSectionCacheAsStale()
    {
        var cache = new DiscoveryCache();
        var ranking = new RankingSummary("26", "Cached chart", "Earlier", "ms-appx:///Assets/Branding/beans-icon.png", [], "qq");
        var playlist = new MusicPlaylist(new MusicIdentity(PlatformId.QqMusic, "88"), "Cached playlist", "QQ Music", new Uri("ms-appx:///Assets/Branding/beans-icon.png"), 12);
        cache.SetValue(new DiscoveryCacheKey("qq", "rankings", string.Empty, 1, "anonymous"),
            (IReadOnlyList<RankingSummary>)[ranking], TimeSpan.FromMilliseconds(1));
        cache.SetValue(new DiscoveryCacheKey("qq", "recommended-playlists", string.Empty, 1, "anonymous"),
            (IReadOnlyList<MusicPlaylist>)[playlist], TimeSpan.FromMilliseconds(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var adapter = FailingAdapter();

        var content = await Service(adapter, cache, false)
            .GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.CacheStale, content.DataOrigin);
        Assert.True(content.IsStale);
        Assert.Equal("网络不可用，正在显示上次内容", content.SafeStatusText);
        Assert.Equal("26", content.Rankings[0].Id);
        Assert.Equal("88", content.RecommendedPlaylists[0].Identity.NativeId);
    }

    [Fact]
    public async Task NoCacheAndNoPreviewReturnsSafeFailure()
    {
        var exception = await Assert.ThrowsAsync<PlatformDiscoveryUnavailableException>(() =>
            Service(FailingAdapter(), new DiscoveryCache(), false)
                .GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Exception", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Network", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitDebugPreviewFallbackRemainsClearlyMarked()
    {
        var content = await Service(FailingAdapter(), new DiscoveryCache(), true)
            .GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.Preview, content.DataOrigin);
        Assert.True(content.IsPreview);
        Assert.Equal("预览内容 · 在线服务尚未连接", content.SafeStatusText);
    }

    [Fact]
    public async Task OneFailedSectionKeepsSuccessfulQqContent()
    {
        var adapter = new ScriptedQqDiscoveryAdapter
        {
            Recommended = (context, _) => Task.FromResult(
                ScriptedQqDiscoveryAdapter.Failure<IReadOnlyList<MusicPlaylist>>(context))
        };

        var content = await Service(adapter, new DiscoveryCache(), false)
            .GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), TestContext.Current.CancellationToken);

        Assert.True(content.IsPartialSuccess);
        Assert.Single(content.Rankings);
        Assert.Empty(content.RecommendedPlaylists);
        Assert.Equal("26", content.HeroContent.TrackId);
        Assert.Contains(content.SectionStates!, state => state.SectionId == "recommended-playlists" && !state.IsSuccess);
    }

    [Fact]
    public async Task CancellationStopsAggregationAndDoesNotPopulateCache()
    {
        var cache = new DiscoveryCache();
        var adapter = new ScriptedQqDiscoveryAdapter();
        adapter.Rankings = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            throw new InvalidOperationException("unreachable");
        };
        adapter.Recommended = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            throw new InvalidOperationException("unreachable");
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(adapter, cache, false).GetDiscoveryAsync("qq", new DiscoveryRequest("qq"), cancellation.Token));

        Assert.False(cache.TryGetValue<IReadOnlyList<RankingSummary>>(
            new DiscoveryCacheKey("qq", "rankings", string.Empty, 1, "anonymous"), true, out _));
        Assert.False(cache.TryGetValue<IReadOnlyList<MusicPlaylist>>(
            new DiscoveryCacheKey("qq", "recommended-playlists", string.Empty, 1, "anonymous"), true, out _));
    }

    [Fact]
    public void CacheSeparatesPlatformCategoryAndPage()
    {
        var cache = new DiscoveryCache();
        var qqRockPage1 = new DiscoveryCacheKey("qq", "playlist-square", "rock", 1, "anonymous");
        var qqRockPage2 = new DiscoveryCacheKey("qq", "playlist-square", "rock", 2, "anonymous");
        var qqPopPage1 = new DiscoveryCacheKey("qq", "playlist-square", "pop", 1, "anonymous");
        var neteaseRockPage1 = new DiscoveryCacheKey("netease", "playlist-square", "rock", 1, "anonymous");
        cache.SetValue(qqRockPage1, "qq-rock-one", TimeSpan.FromMinutes(1));

        Assert.True(cache.TryGetValue<string>(qqRockPage1, false, out _));
        Assert.False(cache.TryGetValue<string>(qqRockPage2, false, out _));
        Assert.False(cache.TryGetValue<string>(qqPopPage1, false, out _));
        Assert.False(cache.TryGetValue<string>(neteaseRockPage1, false, out _));
    }

    [Fact]
    public async Task MissingNetEaseAdapterCanPreviewButUnavailableKugouIsRejected()
    {
        var service = Service(new ScriptedQqDiscoveryAdapter(), new DiscoveryCache(), true);

        var netease = await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.Preview, netease.DataOrigin);
        Assert.Equal("netease", netease.PlatformId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetDiscoveryAsync("kugou", new DiscoveryRequest("kugou"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QqDailyRecommendationsStayEmptyAndUnsupported()
    {
        var result = await Service(new ScriptedQqDiscoveryAdapter(), new DiscoveryCache(), false)
            .GetDailyRecommendationsAsync("qq", TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    private static ScriptedQqDiscoveryAdapter FailingAdapter()
    {
        var adapter = new ScriptedQqDiscoveryAdapter();
        adapter.Rankings = (context, _) => Task.FromResult(
            ScriptedQqDiscoveryAdapter.Failure<IReadOnlyList<RankingSummary>>(context));
        adapter.Recommended = (context, _) => Task.FromResult(
            ScriptedQqDiscoveryAdapter.Failure<IReadOnlyList<MusicPlaylist>>(context));
        return adapter;
    }

    private static QqPlatformDiscoveryService Service(
        IPlatformDiscoveryAdapter adapter,
        IDiscoveryCache cache,
        bool previewEnabled)
    {
        var policy = new FixedPreviewModePolicy(previewEnabled);
        var registry = Registry();
        var preview = new PreviewDiscoveryService(cache, registry, policy);
        return new QqPlatformDiscoveryService([adapter], cache, registry, preview, policy);
    }

    private static MusicPlatformRegistry Registry()
    {
        var services = new[]
        {
            Platform(PlatformId.QqMusic, "QQ 音乐"),
            Platform(PlatformId.NetEaseMusic, "网易云音乐"),
            Platform(PlatformId.KuGouMusic, "酷狗音乐", false)
        };
        return new MusicPlatformRegistry(services, new MemoryPreferenceStore());
    }

    private static IMusicPlatformService Platform(PlatformId id, string name, bool available = true) =>
        new PreviewMusicPlatformService(new MusicPlatformDescriptor(
            id, name, name, name[..1], "#0A8F66", available, available, AuthorizationState.SignedOut,
            available, false, available, available, available && id != PlatformId.KuGouMusic,
            available ? "可浏览公开内容" : "当前版本暂不支持酷狗音乐"));

    private sealed class MemoryPreferenceStore : IPlatformPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? GetString(string key) => _values.GetValueOrDefault(key);
        public void SetString(string key, string value) => _values[key] = value;
    }
}
