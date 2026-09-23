using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.PreviewData;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.ViewModels;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PlatformInfrastructureTests
{
    [Fact]
    public void PlatformStableIdsAreUnique()
    {
        var ids = OnlineDescriptors().Select(platform => platform.StableId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(["qq", "netease", "kugou"], ids);
    }

    [Fact]
    public void CurrentPlatformPersistsAcrossRegistryInstances()
    {
        var store = new MemoryPreferenceStore();
        var first = Registry(store);
        first.SetCurrentPlatform("netease");
        var second = Registry(store);
        Assert.Equal(PlatformId.NetEaseMusic, second.CurrentPlatformId);
    }

    [Fact]
    public void DisablingCurrentPlatformFallsBackToEnabledPlatform()
    {
        var registry = Registry(new MemoryPreferenceStore());
        registry.SetCurrentPlatform("qq");
        registry.SetEnabled("qq", false);
        Assert.NotEqual(PlatformId.QqMusic, registry.CurrentPlatformId);
        Assert.True(registry.GetCurrentPlatform()!.IsEnabled);
    }

    [Fact]
    public void RegistryKeepsAtLeastOneOnlinePlatformEnabled()
    {
        var registry = Registry(new MemoryPreferenceStore());
        registry.SetEnabled("netease", false);
        registry.SetEnabled("kugou", false);
        registry.SetEnabled("qq", false);
        Assert.Single(registry.GetEnabledPlatforms());
    }

    [Fact]
    public void CacheKeysDoNotCollideAcrossPlatforms()
    {
        var cache = new DiscoveryCache();
        var qq = new DiscoveryCacheKey("qq", "discovery", "推荐", 1, "anonymous");
        var netease = new DiscoveryCacheKey("netease", "discovery", "推荐", 1, "anonymous");
        cache.Set(qq, QqDiscoveryPreviewData.Create(), TimeSpan.FromMinutes(1));
        Assert.True(cache.TryGet(qq, out _));
        Assert.False(cache.TryGet(netease, out _));
    }

    [Fact]
    public async Task PlatformsKeepIndependentScrollOffsets()
    {
        var registry = Registry(new MemoryPreferenceStore());
        var viewModel = ViewModel(registry);
        await viewModel.InitializeAsync();
        viewModel.SaveScrollOffset(320);
        await viewModel.SwitchPlatformAsync("netease");
        viewModel.SaveScrollOffset(144);
        await viewModel.SwitchPlatformAsync("qq");
        Assert.Equal(320, viewModel.CurrentState.ScrollOffset);
        Assert.Equal(144, viewModel.States["netease"].ScrollOffset);
    }

    [Fact]
    public async Task FastSwitchCannotExposeOldPlatformContent()
    {
        var registry = Registry(new MemoryPreferenceStore());
        var viewModel = new DiscoverViewModel(registry, new DelayedDiscoveryService());
        var firstLoad = viewModel.InitializeAsync();
        await Task.Delay(10, TestContext.Current.CancellationToken);
        await viewModel.SwitchPlatformAsync("netease");
        await firstLoad;
        Assert.Equal("netease", viewModel.CurrentPlatformId);
        Assert.Equal("netease", viewModel.Content!.PlatformId);
        Assert.Null(viewModel.States["qq"].ErrorState);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeAnError()
    {
        var registry = Registry(new MemoryPreferenceStore());
        var viewModel = new DiscoverViewModel(registry, new DelayedDiscoveryService());
        var firstLoad = viewModel.InitializeAsync();
        await Task.Delay(10, TestContext.Current.CancellationToken);
        await viewModel.SwitchPlatformAsync("kugou");
        await firstLoad;
        Assert.Null(viewModel.States["qq"].ErrorState);
    }

    [Fact]
    public void PreviewDataIsDistinctAndMarkedPartial()
    {
        var qq = QqDiscoveryPreviewData.Create();
        var netease = NetEaseDiscoveryPreviewData.Create();
        var kugou = KuGouDiscoveryPreviewData.Create();
        Assert.Equal(3, new[] { qq.HeroContent.Title, netease.HeroContent.Title, kugou.HeroContent.Title }.Distinct().Count());
        Assert.All(new[] { qq, netease, kugou }, content => Assert.True(content.IsPartialSuccess));
    }

    [Fact]
    public void UnauthorizedDailyRecommendationsAreNotFabricated()
    {
        Assert.Empty(QqDiscoveryPreviewData.Create().DailyRecommendations);
        Assert.Empty(NetEaseDiscoveryPreviewData.Create().DailyRecommendations);
        Assert.Empty(KuGouDiscoveryPreviewData.Create().DailyRecommendations);
    }

    [Fact]
    public void UnknownPlatformUsesUnknownSourceLabel() =>
        Assert.Equal("未知来源", ((PlatformId)999).ToDisplayName());

    [Fact]
    public void UnavailableKugouCannotBeEnabledOrNotifySubscribers()
    {
        var registry = Registry(new MemoryPreferenceStore());
        PlatformStateChangedEventArgs? observed = null;
        registry.PlatformStateChanged += (_, args) => observed = args;
        registry.SetEnabled("kugou", true);
        Assert.Null(observed);
        Assert.False(registry.IsEnabled("kugou"));
    }

    [Fact]
    public async Task CategoriesRemainIndependentPerPlatform()
    {
        var registry = Registry(new MemoryPreferenceStore());
        var viewModel = ViewModel(registry);
        await viewModel.InitializeAsync();
        await viewModel.SelectCategoryAsync("流行");
        await viewModel.SwitchPlatformAsync("netease");
        await viewModel.SelectCategoryAsync("摇滚");
        Assert.Equal("流行", viewModel.States["qq"].SelectedCategory);
        Assert.Equal("摇滚", viewModel.States["netease"].SelectedCategory);
    }

    [Fact]
    public void DetailRouteCarriesPlatformAndNativeIdentity()
    {
        var route = new PlatformRouteParameter("qq", "ranking-1", "榜单");
        Assert.Equal("qq", route.PlatformId);
        Assert.Equal("ranking-1", route.NativeId);
    }

    private static DiscoverViewModel ViewModel(IMusicPlatformRegistry registry) => new(
        registry,
        new PreviewDiscoveryService(new DiscoveryCache(), registry, new FixedPreviewModePolicy(true)));

    private static MusicPlatformRegistry Registry(IPlatformPreferenceStore store)
    {
        var services = OnlineDescriptors().Select(descriptor => (IMusicPlatformService)new PreviewMusicPlatformService(descriptor)).ToArray();
        return new MusicPlatformRegistry(services, store);
    }

    private static MusicPlatformDescriptor[] OnlineDescriptors() =>
    [
        Descriptor(PlatformId.QqMusic, "QQ 音乐"),
        Descriptor(PlatformId.NetEaseMusic, "网易云音乐"),
        Descriptor(PlatformId.KuGouMusic, "酷狗音乐", false)
    ];

    private static MusicPlatformDescriptor Descriptor(PlatformId id, string name, bool available = true) => new(
        id, name, name, name[..1], "#0A8F66", available, available, AuthorizationState.SignedOut,
        available, available, available, available, available,
        available ? "可浏览公开内容" : "当前版本暂不支持酷狗音乐");

    private sealed class MemoryPreferenceStore : IPlatformPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? GetString(string key) => _values.GetValueOrDefault(key);
        public void SetString(string key, string value) => _values[key] = value;
    }

    private sealed class DelayedDiscoveryService : IMusicDiscoveryService
    {
        public async Task<PlatformDiscoveryContent> GetDiscoveryAsync(string platformId, DiscoveryRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(platformId == "qq" ? 180 : 5, cancellationToken);
            return platformId switch { "qq" => QqDiscoveryPreviewData.Create(), "netease" => NetEaseDiscoveryPreviewData.Create(), _ => KuGouDiscoveryPreviewData.Create() };
        }
        public async Task<IReadOnlyList<RankingSummary>> GetRankingsAsync(string platformId, CancellationToken cancellationToken) => (await GetDiscoveryAsync(platformId, new(platformId), cancellationToken)).Rankings;
        public async Task<IReadOnlyList<MusicPlaylist>> GetRecommendedPlaylistsAsync(string platformId, CancellationToken cancellationToken) => (await GetDiscoveryAsync(platformId, new(platformId), cancellationToken)).RecommendedPlaylists;
        public async Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(string platformId, CancellationToken cancellationToken) => (await GetDiscoveryAsync(platformId, new(platformId), cancellationToken)).DailyRecommendations;
        public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistSquareAsync(string platformId, string? category, int page, CancellationToken cancellationToken) => (await GetDiscoveryAsync(platformId, new(platformId, category, page), cancellationToken)).PlaylistSquare;
        public void Invalidate(string platformId) { }
    }
}
