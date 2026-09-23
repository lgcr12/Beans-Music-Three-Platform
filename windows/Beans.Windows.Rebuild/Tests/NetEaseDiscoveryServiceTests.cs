using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEaseDiscoveryServiceTests
{
    [Fact]
    public async Task LiveSectionsAreAggregatedWithDailyAuthorizationBoundary()
    {
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        var content = await Service(adapter, new DiscoveryCache(), false)
            .GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚"), TestContext.Current.CancellationToken);

        Assert.Equal("netease", content.PlatformId);
        Assert.Equal(DiscoveryDataOrigin.Live, content.DataOrigin);
        Assert.False(content.IsPreview);
        Assert.Single(content.Rankings);
        Assert.Single(content.RecommendedPlaylists);
        Assert.Single(content.PlaylistSquare);
        Assert.Equal(["华语", "摇滚"], content.Categories);
        Assert.Equal("910001", content.HeroContent.TrackId);
        Assert.True(content.IsPartialSuccess);
        Assert.Contains(content.SectionStates!, section =>
            section.SectionId == "daily-recommendations" && section.ErrorCode == PlatformErrorCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceCreatesNetEaseScopedOperationsAndSafeKeys()
    {
        string? operation = null;
        string? key = null;
        var adapter = new ScriptedNetEaseDiscoveryAdapter
        {
            Rankings = (context, _) =>
            {
                operation = context.OperationName;
                key = context.RequestKey;
                return Task.FromResult(ScriptedNetEaseDiscoveryAdapter.Success<IReadOnlyList<RankingSummary>>([], context));
            }
        };

        await Service(adapter, new DiscoveryCache(), false)
            .GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken);

        Assert.Equal("netease.discovery.rankings", operation);
        Assert.Equal(NetEaseDiscoveryRequestKeys.Rankings, key);
        RequestKeyValidator.Validate("netease", key!);
    }

    [Fact]
    public async Task SecondLoadUsesFreshCachesWithoutRepeatingPublicRequests()
    {
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);
        await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚"), TestContext.Current.CancellationToken);

        var cached = await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.CacheFresh, cached.DataOrigin);
        Assert.Equal(1, adapter.RankingCallCount);
        Assert.Equal(1, adapter.RecommendedCallCount);
        Assert.Equal(1, adapter.CategoryCallCount);
        Assert.Equal(1, adapter.SquareCallCount);
    }

    [Fact]
    public async Task ForceRefreshBypassesFreshCaches()
    {
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);
        await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚"), TestContext.Current.CancellationToken);
        adapter.Recommended = (context, _) => Task.FromResult(ScriptedNetEaseDiscoveryAdapter.Success<IReadOnlyList<MusicPlaylist>>(
            [new MusicPlaylist(new MusicIdentity(PlatformId.NetEaseMusic, "910099"), "Refreshed", "NetEase", null, null)], context));

        var refreshed = await service.GetDiscoveryAsync(
            "netease", new DiscoveryRequest("netease", "摇滚", ForceRefresh: true), TestContext.Current.CancellationToken);

        Assert.Equal(2, adapter.RecommendedCallCount);
        Assert.Equal("910099", refreshed.RecommendedPlaylists[0].Identity.NativeId);
        Assert.Equal(DiscoveryDataOrigin.Live, refreshed.DataOrigin);
    }

    [Fact]
    public async Task PlaylistSquareCacheSeparatesCategoryAndPage()
    {
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);

        await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚", 1), TestContext.Current.CancellationToken);
        await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "流行", 1), TestContext.Current.CancellationToken);
        await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚", 2), TestContext.Current.CancellationToken);
        var cached = await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease", "摇滚", 1), TestContext.Current.CancellationToken);

        Assert.Equal(3, adapter.SquareCallCount);
        Assert.Equal("摇滚-1", cached.PlaylistSquare[0].Identity.NativeId);
    }

    [Fact]
    public async Task FailedRefreshKeepsFreshCacheAndPartialFailureKeepsOtherSections()
    {
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        var service = Service(adapter, new DiscoveryCache(), false);
        await service.GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken);
        adapter.Recommended = (context, _) => Task.FromResult(
            ScriptedNetEaseDiscoveryAdapter.Failure<IReadOnlyList<MusicPlaylist>>(context));

        var fallback = await service.GetDiscoveryAsync(
            "netease", new DiscoveryRequest("netease", ForceRefresh: true), TestContext.Current.CancellationToken);

        Assert.Equal("910001", fallback.RecommendedPlaylists[0].Identity.NativeId);
        Assert.Single(fallback.Rankings);
        Assert.Equal(DiscoveryDataOrigin.Live, fallback.DataOrigin);
        Assert.True(fallback.IsPartialSuccess);
    }

    [Fact]
    public async Task ExpiredCacheBecomesStaleWhenNetworkFails()
    {
        var cache = new DiscoveryCache();
        cache.SetValue(new DiscoveryCacheKey("netease", "rankings", string.Empty, 1, "anonymous"),
            (IReadOnlyList<RankingSummary>)[new RankingSummary("19723756", "Cached", "Earlier", "", [], "netease")],
            TimeSpan.FromMilliseconds(1));
        cache.SetValue(new DiscoveryCacheKey("netease", "recommended-playlists", string.Empty, 1, "anonymous"),
            (IReadOnlyList<MusicPlaylist>)[new MusicPlaylist(new MusicIdentity(PlatformId.NetEaseMusic, "910001"), "Cached", "NetEase", null, null)],
            TimeSpan.FromMilliseconds(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var adapter = FailingAdapter();

        var content = await Service(adapter, cache, false)
            .GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.CacheStale, content.DataOrigin);
        Assert.True(content.IsStale);
        Assert.Equal("网络不可用，正在显示上次内容", content.SafeStatusText);
    }

    [Fact]
    public async Task NoCacheAndNoPreviewReturnsSafeFailure()
    {
        var exception = await Assert.ThrowsAsync<PlatformDiscoveryUnavailableException>(() =>
            Service(FailingAdapter(), new DiscoveryCache(), false)
                .GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("Exception", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitDebugPreviewFallbackIsClearlyMarked()
    {
        var content = await Service(FailingAdapter(), new DiscoveryCache(), true)
            .GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken);

        Assert.Equal(DiscoveryDataOrigin.Preview, content.DataOrigin);
        Assert.True(content.IsPreview);
    }

    [Fact]
    public async Task CancellationStopsAggregationAndDoesNotPopulateCache()
    {
        var cache = new DiscoveryCache();
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        adapter.Rankings = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            throw new InvalidOperationException("unreachable");
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(adapter, cache, false).GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), cancellation.Token));

        Assert.False(cache.TryGetValue<IReadOnlyList<RankingSummary>>(
            new DiscoveryCacheKey("netease", "rankings", string.Empty, 1, "anonymous"), true, out _));
    }

    [Fact]
    public async Task NetEaseAndQqCachesRemainIndependent()
    {
        var cache = new DiscoveryCache();
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        await Service(adapter, cache, false)
            .GetDiscoveryAsync("netease", new DiscoveryRequest("netease"), TestContext.Current.CancellationToken);

        Assert.True(cache.TryGetValue<IReadOnlyList<RankingSummary>>(
            new DiscoveryCacheKey("netease", "rankings", string.Empty, 1, "anonymous"), false, out _));
        Assert.False(cache.TryGetValue<IReadOnlyList<RankingSummary>>(
            new DiscoveryCacheKey("qq", "rankings", string.Empty, 1, "anonymous"), false, out _));
    }

    private static ScriptedNetEaseDiscoveryAdapter FailingAdapter()
    {
        var adapter = new ScriptedNetEaseDiscoveryAdapter();
        adapter.Rankings = (context, _) => Task.FromResult(ScriptedNetEaseDiscoveryAdapter.Failure<IReadOnlyList<RankingSummary>>(context));
        adapter.Recommended = (context, _) => Task.FromResult(ScriptedNetEaseDiscoveryAdapter.Failure<IReadOnlyList<MusicPlaylist>>(context));
        adapter.Categories = (context, _) => Task.FromResult(ScriptedNetEaseDiscoveryAdapter.Failure<IReadOnlyList<string>>(context));
        adapter.Square = (_, _, context, _) => Task.FromResult(ScriptedNetEaseDiscoveryAdapter.Failure<IReadOnlyList<MusicPlaylist>>(context));
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
            available, available && id == PlatformId.NetEaseMusic, available, available, available,
            available ? "可浏览公开内容" : "当前版本暂不支持酷狗音乐"));

    private sealed class MemoryPreferenceStore : IPlatformPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? GetString(string key) => _values.GetValueOrDefault(key);
        public void SetString(string key, string value) => _values[key] = value;
    }
}
