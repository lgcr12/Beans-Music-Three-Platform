using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.PreviewData;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.ViewModels;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class Phase5EDualPlatformTests
{
    private static readonly DateTimeOffset LoadedAt = new(2026, 9, 20, 10, 24, 0, TimeSpan.Zero);

    [Fact]
    public void VisibleDiscoveryPlatformsAreExactlyQqAndNetEase()
    {
        var registry = Registry(new MemoryPreferenceStore());
        using var viewModel = new DiscoverViewModel(registry, new StaticDiscoveryService());

        Assert.Equal(["qq", "netease"], viewModel.Platforms.Select(platform => platform.StableId));
        var kugou = registry.GetPlatform("kugou");
        Assert.NotNull(kugou);
        Assert.False(kugou.IsAvailable);
        Assert.False(kugou.IsEnabled);
        Assert.Equal("当前版本暂不支持酷狗音乐", kugou.SafeStatusText);
    }

    [Fact]
    public void HistoricalKugouPreferenceFallsBackToQqAndPersistsMigration()
    {
        var store = new MemoryPreferenceStore
        {
            ["platform.current"] = "kugou",
            ["platform.enabled.kugou"] = "True"
        };

        var registry = Registry(store);

        Assert.Equal(PlatformId.QqMusic, registry.CurrentPlatformId);
        Assert.Equal("qq", store["platform.current"]);
        Assert.Equal("False", store["platform.enabled.kugou"]);
    }

    [Fact]
    public void KugouCannotBeEnabledOrSelected()
    {
        var store = new MemoryPreferenceStore();
        var registry = Registry(store);

        registry.SetEnabled("kugou", true);
        registry.SetCurrentPlatform("kugou");

        Assert.False(registry.IsEnabled("kugou"));
        Assert.Equal(PlatformId.QqMusic, registry.CurrentPlatformId);
        Assert.Equal("qq", store["platform.current"]);
    }

    [Theory]
    [InlineData("qq", DiscoveryDataOrigin.Live, "QQ 音乐 · 公开内容 · 更新于 10:24")]
    [InlineData("netease", DiscoveryDataOrigin.Live, "网易云音乐 · 公开内容 · 更新于 10:24")]
    [InlineData("qq", DiscoveryDataOrigin.CacheFresh, "QQ 音乐 · 来自缓存 · 更新于 10:24")]
    [InlineData("netease", DiscoveryDataOrigin.CacheFresh, "网易云音乐 · 来自缓存 · 更新于 10:24")]
    [InlineData("qq", DiscoveryDataOrigin.CacheStale, "QQ 音乐 · 网络不可用，正在显示上次内容")]
    [InlineData("netease", DiscoveryDataOrigin.CacheStale, "网易云音乐 · 网络不可用，正在显示上次内容")]
    [InlineData("qq", DiscoveryDataOrigin.Preview, "QQ 音乐 · 预览内容 · 在线服务尚未连接")]
    [InlineData("netease", DiscoveryDataOrigin.Preview, "网易云音乐 · 预览内容 · 在线服务尚未连接")]
    public async Task DiscoveryStatusTextMatchesOriginExactly(string platformId, DiscoveryDataOrigin origin, string expected)
    {
        var store = new MemoryPreferenceStore { ["platform.current"] = platformId };
        using var viewModel = new DiscoverViewModel(
            Registry(store),
            new StaticDiscoveryService((id, _) => Content(id, origin)));

        await viewModel.InitializeAsync();

        Assert.Equal(expected, viewModel.StatusText);
    }

    [Theory]
    [InlineData("qq", PlatformErrorCode.Unsupported, "当前版本暂不支持 QQ 音乐每日推荐")]
    [InlineData("qq", PlatformErrorCode.Unauthorized, "登录 QQ 音乐后查看每日推荐")]
    [InlineData("netease", PlatformErrorCode.Unauthorized, "登录网易云音乐后查看每日推荐")]
    public async Task DailyRecommendationMessageFollowsPlatformCapability(
        string platformId,
        PlatformErrorCode errorCode,
        string expected)
    {
        var store = new MemoryPreferenceStore { ["platform.current"] = platformId };
        using var viewModel = new DiscoverViewModel(
            Registry(store),
            new StaticDiscoveryService((id, _) => Content(id, DiscoveryDataOrigin.Live, errorCode)));

        await viewModel.InitializeAsync();

        Assert.Equal(expected, viewModel.DailyRecommendationStatusText);
    }

    [Theory]
    [InlineData(PlatformId.QqMusic, "QQ 音乐")]
    [InlineData(PlatformId.NetEaseMusic, "网易云音乐")]
    [InlineData(PlatformId.Local, "本地音乐")]
    [InlineData(PlatformId.Beans, "Beans 歌单")]
    [InlineData((PlatformId)999, "未知来源")]
    public void SourceDisplayNamesAreCanonical(PlatformId platform, string expected) =>
        Assert.Equal(expected, platform.ToDisplayName());

    [Fact]
    public void UnknownRankingSourceIsNotMislabelledAsLocalMusic()
    {
        var ranking = new RankingSummary("unknown", "Unknown", "", "", [], "other");

        Assert.Equal("未知来源", ranking.Platform.ToDisplayName());
        Assert.NotEqual(PlatformId.Local, ranking.Platform);
    }

    [Fact]
    public void HomePreviewUsesOnlySupportedOrInternalSources()
    {
        var sources = HomePreviewData.Playlists.Select(item => item.SourcePlatform)
            .Concat(HomePreviewData.RecentTracks.Select(item => item.SourcePlatform))
            .Concat(HomePreviewData.Recommendations.Select(item => item.SourcePlatform))
            .ToArray();

        Assert.DoesNotContain(PlatformId.KuGouMusic, sources);
        Assert.Contains(PlatformId.QqMusic, sources);
        Assert.Contains(PlatformId.NetEaseMusic, sources);
        Assert.Contains(PlatformId.Local, sources);
        Assert.Contains(PlatformId.Beans, sources);
        Assert.Equal("本地音乐 · 预览内容", HomePreviewData.CurrentPlaybackItem.SourceLabel);
    }

    [Fact]
    public async Task PlatformContentScrollRefreshAndErrorsRemainIndependent()
    {
        var service = new ControllableDiscoveryService();
        using var viewModel = new DiscoverViewModel(Registry(new MemoryPreferenceStore()), service);
        await viewModel.InitializeAsync();
        viewModel.SaveScrollOffset(311);

        await viewModel.SwitchPlatformAsync("netease");
        viewModel.SaveScrollOffset(127);

        Assert.Equal("qq-hero", viewModel.States["qq"].Content!.HeroContent.Title);
        Assert.Equal("qq-ranking", viewModel.States["qq"].Content!.Rankings[0].Title);
        Assert.Equal("qq-playlist", viewModel.States["qq"].Content!.RecommendedPlaylists[0].Title);
        Assert.Equal("netease-hero", viewModel.States["netease"].Content!.HeroContent.Title);
        Assert.Equal("netease-ranking", viewModel.States["netease"].Content!.Rankings[0].Title);
        Assert.Equal("netease-playlist", viewModel.States["netease"].Content!.RecommendedPlaylists[0].Title);

        await viewModel.SwitchPlatformAsync("qq");
        service.BlockQqRefresh = true;
        var refresh = viewModel.RefreshAsync();
        await service.RefreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.States["qq"].IsRefreshing);
        Assert.False(viewModel.States["netease"].IsRefreshing);
        service.ReleaseRefresh.TrySetResult();
        await refresh;

        service.FailQq = true;
        await viewModel.RefreshAsync();
        await viewModel.SwitchPlatformAsync("netease");

        Assert.Equal(311, viewModel.States["qq"].ScrollOffset);
        Assert.Equal(127, viewModel.States["netease"].ScrollOffset);
        Assert.NotNull(viewModel.States["qq"].ErrorState);
        Assert.Null(viewModel.States["netease"].ErrorState);
        Assert.Equal("netease", viewModel.Content!.PlatformId);
    }

    private static MusicPlatformRegistry Registry(IPlatformPreferenceStore store)
    {
        var services = new[]
        {
            Service(PlatformId.QqMusic, "QQ 音乐", true),
            Service(PlatformId.NetEaseMusic, "网易云音乐", true),
            Service(PlatformId.KuGouMusic, "酷狗音乐", false)
        };
        return new MusicPlatformRegistry(services, store);
    }

    private static IMusicPlatformService Service(PlatformId id, string name, bool available) =>
        new PreviewMusicPlatformService(new MusicPlatformDescriptor(
            id, name, name, name[..1], "#0A8F66", available, available, AuthorizationState.SignedOut,
            available, available && id == PlatformId.NetEaseMusic, available, available, available,
            available ? "可浏览公开内容" : "当前版本暂不支持酷狗音乐"));

    private static PlatformDiscoveryContent Content(
        string platformId,
        DiscoveryDataOrigin origin,
        PlatformErrorCode? dailyError = null)
    {
        var platform = platformId == "qq" ? PlatformId.QqMusic : PlatformId.NetEaseMusic;
        IReadOnlyList<DiscoverySectionState> sections = dailyError is { } error
            ? [new DiscoverySectionState("daily-recommendations", false, error, "daily", origin, LoadedAt, false)]
            : [];
        return new PlatformDiscoveryContent(
            platformId,
            new DiscoveryHero($"{platformId}-hero", "subtitle", "ms-appx:///Assets/Branding/beans-icon.png", "open"),
            [],
            [new RankingSummary($"{platformId}-ranking-id", $"{platformId}-ranking", "updated", "", [], platformId)],
            [new MusicPlaylist(new MusicIdentity(platform, $"{platformId}-playlist-id"), $"{platformId}-playlist", "creator", null, null)],
            [],
            ["推荐"],
            false,
            dailyError is not null,
            LoadedAt,
            "loaded",
            origin,
            origin == DiscoveryDataOrigin.CacheStale,
            origin == DiscoveryDataOrigin.Preview,
            "status",
            sections);
    }

    private sealed class MemoryPreferenceStore : IPlatformPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? this[string key]
        {
            get => _values.GetValueOrDefault(key);
            set
            {
                if (value is null) _values.Remove(key);
                else _values[key] = value;
            }
        }

        public string? GetString(string key) => this[key];
        public void SetString(string key, string value) => this[key] = value;
    }

    private class StaticDiscoveryService(
        Func<string, DiscoveryRequest, PlatformDiscoveryContent>? factory = null) : IMusicDiscoveryService
    {
        protected virtual Task<PlatformDiscoveryContent> LoadAsync(string platformId, DiscoveryRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(factory?.Invoke(platformId, request) ?? Content(platformId, DiscoveryDataOrigin.Live));
        }

        public Task<PlatformDiscoveryContent> GetDiscoveryAsync(string platformId, DiscoveryRequest request, CancellationToken cancellationToken) =>
            LoadAsync(platformId, request, cancellationToken);
        public async Task<IReadOnlyList<RankingSummary>> GetRankingsAsync(string platformId, CancellationToken cancellationToken) =>
            (await LoadAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).Rankings;
        public async Task<IReadOnlyList<MusicPlaylist>> GetRecommendedPlaylistsAsync(string platformId, CancellationToken cancellationToken) =>
            (await LoadAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).RecommendedPlaylists;
        public async Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(string platformId, CancellationToken cancellationToken) =>
            (await LoadAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).DailyRecommendations;
        public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistSquareAsync(string platformId, string? category, int page, CancellationToken cancellationToken) =>
            (await LoadAsync(platformId, new DiscoveryRequest(platformId, category, page), cancellationToken)).PlaylistSquare;
        public void Invalidate(string platformId) { }
    }

    private sealed class ControllableDiscoveryService : StaticDiscoveryService
    {
        public bool BlockQqRefresh { get; set; }
        public bool FailQq { get; set; }
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<PlatformDiscoveryContent> LoadAsync(
            string platformId,
            DiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            if (platformId == "qq" && request.ForceRefresh && BlockQqRefresh)
            {
                RefreshStarted.TrySetResult();
                await ReleaseRefresh.Task.WaitAsync(cancellationToken);
                BlockQqRefresh = false;
            }

            if (platformId == "qq" && request.ForceRefresh && FailQq)
                throw new PlatformDiscoveryUnavailableException("QQ 音乐公开内容暂时无法加载");
            return Content(platformId, DiscoveryDataOrigin.Live);
        }
    }
}
