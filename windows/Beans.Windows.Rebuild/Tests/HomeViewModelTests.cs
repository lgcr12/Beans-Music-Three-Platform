using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.ViewModels;
using CatalogTrack = Beans.Windows.Rebuild.Services.LocalMusic.LocalTrack;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class HomeViewModelTests
{
    [Fact]
    public async Task PreviewDiscoveryIsNeverPresentedAsRealHomeContent()
    {
        using var viewModel = new HomeViewModel(
            new FakeDiscoveryService((platform, _) => Content(platform, preview: true)),
            new FakeLibraryService(Snapshot()),
            new FakeLocalCatalog([]));

        await viewModel.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(viewModel.Playlists);
        Assert.Empty(viewModel.Recommendations);
        Assert.False(viewModel.HasHeroTarget);
        Assert.Contains("暂时不可用", viewModel.PlaylistStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveDiscoveryAlternatesProvidersAndRoutesHeroToRealPlaylist()
    {
        using var viewModel = new HomeViewModel(
            new FakeDiscoveryService((platform, _) => Content(platform, preview: false)),
            new FakeLibraryService(Snapshot()),
            new FakeLocalCatalog([]));

        await viewModel.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, viewModel.Playlists.Count);
        Assert.Equal(
            [PlatformId.QqMusic, PlatformId.NetEaseMusic, PlatformId.QqMusic, PlatformId.NetEaseMusic],
            viewModel.Playlists.Select(item => item.SourcePlatform));
        Assert.NotNull(viewModel.HeroTarget);
        Assert.Equal("playlist", viewModel.HeroTarget!.Route);
        Assert.Equal("qq", viewModel.HeroTarget.Parameter.PlatformId);
        Assert.DoesNotContain(viewModel.Playlists, item => item.Title.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealLibraryHistoryAndFavoriteCountPopulateWithoutPlaybackAddressPersistence()
    {
        var item = new LibraryMediaSnapshot(
            LibraryItemKind.Track, PlatformId.NetEaseMusic, "188", "历史歌曲", "歌手", "专辑",
            "https://images.example/188.jpg", 123_000, "HQ", SearchDataOrigin.Live);
        var snapshot = Snapshot(
            [new FavoriteEntry(item, DateTimeOffset.UtcNow)],
            [new PlaybackHistoryEntry(item, DateTimeOffset.UtcNow, 40_000, 2)]);
        using var viewModel = new HomeViewModel(
            new FakeDiscoveryService((platform, _) => Content(platform, preview: false, includeDaily: false)),
            new FakeLibraryService(snapshot),
            new FakeLocalCatalog([]));

        await viewModel.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var track = Assert.Single(viewModel.RecentTracks);
        Assert.Equal("历史歌曲", track.Title);
        Assert.Equal(SearchDataOrigin.Live, track.PlaybackTarget.DataOrigin);
        Assert.Null(track.PlaybackTarget.PlaybackUri);
        Assert.Equal("本机收藏 · 1 项", viewModel.FavoriteSummaryText);
    }

    private static PlatformDiscoveryContent Content(string platformId, bool preview, bool includeDaily = true)
    {
        var platform = platformId == "qq" ? PlatformId.QqMusic : PlatformId.NetEaseMusic;
        var playlists = Enumerable.Range(1, 3)
            .Select(index => new MusicPlaylist(
                new MusicIdentity(platform, $"{platformId}-{index}"),
                $"{platform.ToDisplayName()}歌单 {index}",
                $"创作者 {index}",
                new Uri($"https://images.example/{platformId}-{index}.jpg"),
                index * 10,
                PlayCount: index * 12_345,
                SourceBadgeText: platform.ToDisplayName()))
            .ToArray();
        var daily = includeDaily
            ? new[]
            {
                new MusicTrack(
                    new MusicIdentity(platform, $"{platformId}-track"),
                    $"{platform.ToDisplayName()}推荐歌曲",
                    [new MusicArtist(new MusicIdentity(platform, $"{platformId}-artist"), "真实歌手", null)],
                    null,
                    new Uri($"https://images.example/{platformId}-track.jpg"),
                    TimeSpan.FromMinutes(3),
                    AvailabilityState.Available,
                    AudioQuality.High)
            }
            : [];
        return new PlatformDiscoveryContent(
            platformId,
            new DiscoveryHero(playlists[0].Title, "公开推荐歌单", playlists[0].CoverUri, "查看歌单", playlists[0].Identity.NativeId),
            daily,
            [],
            playlists,
            [],
            [],
            false,
            false,
            DateTimeOffset.UtcNow,
            "已加载",
            preview ? DiscoveryDataOrigin.Preview : DiscoveryDataOrigin.Live,
            false,
            preview,
            preview ? "预览内容" : "公开内容已更新");
    }

    private static UserLibrarySnapshot Snapshot(
        IReadOnlyList<FavoriteEntry>? favorites = null,
        IReadOnlyList<PlaybackHistoryEntry>? history = null) =>
        new(favorites ?? [], history ?? [], LibraryRecoveryStatus.Normal, DateTimeOffset.UtcNow);

    private sealed class FakeDiscoveryService(
        Func<string, DiscoveryRequest, PlatformDiscoveryContent> factory) : IMusicDiscoveryService
    {
        public Task<PlatformDiscoveryContent> GetDiscoveryAsync(string platformId, DiscoveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(platformId, request));
        public async Task<IReadOnlyList<RankingSummary>> GetRankingsAsync(string platformId, CancellationToken cancellationToken) =>
            (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).Rankings;
        public async Task<IReadOnlyList<MusicPlaylist>> GetRecommendedPlaylistsAsync(string platformId, CancellationToken cancellationToken) =>
            (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).RecommendedPlaylists;
        public async Task<IReadOnlyList<MusicTrack>> GetDailyRecommendationsAsync(string platformId, CancellationToken cancellationToken) =>
            (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId), cancellationToken)).DailyRecommendations;
        public async Task<IReadOnlyList<MusicPlaylist>> GetPlaylistSquareAsync(string platformId, string? category, int page, CancellationToken cancellationToken) =>
            (await GetDiscoveryAsync(platformId, new DiscoveryRequest(platformId, category, page), cancellationToken)).PlaylistSquare;
        public void Invalidate(string platformId) { }
    }

    private sealed class FakeLibraryService(UserLibrarySnapshot snapshot) : IUserLibraryService
    {
        public Task<UserLibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<bool> IsFavoriteAsync(LibraryItemKind kind, PlatformId platform, string nativeId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SetFavoriteAsync(LibraryMediaSnapshot item, bool isFavorite, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RecordPlaybackAsync(LibraryMediaSnapshot item, TimeSpan playedDuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearPlaybackHistoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeLocalCatalog(IReadOnlyList<CatalogTrack> tracks) : ILocalMusicCatalog
    {
        public event EventHandler<LocalScanProgress>? ProgressChanged { add { } remove { } }
        public bool IsAvailable => tracks.Count > 0;
        public LocalScanProgress ScanState { get; } = new(LocalScanStatus.Idle, null, null, 0, 0, 0, 0, 0, 0, null, "空闲");
        public Task<LocalMusicFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveFolderAsync(string pathOrId, bool removeIndex = true, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<LocalMusicFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalMusicFolder>>([]);
        public Task<LocalScanSummary> ScanAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogTrack?> GetTrackAsync(string stableId, CancellationToken cancellationToken = default) => Task.FromResult(tracks.FirstOrDefault(item => item.Id == stableId));
        public Task<IReadOnlyList<CatalogTrack>> GetTracksAsync(bool includeMissing = false, CancellationToken cancellationToken = default) => Task.FromResult(tracks);
        public Task<IReadOnlyList<CatalogTrack>> GetRecentlyAddedAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogTrack>>(tracks.Take(limit).ToArray());
        public Task<IReadOnlyList<CatalogTrack>> GetRecentlyPlayedAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogTrack>>(tracks.Where(item => item.LastPlayedAt is not null).Take(limit).ToArray());
        public Task<int> RemoveMissingFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task RecordPlaybackStartedAsync(string stableId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordPlaybackProgressAsync(string stableId, long playedMilliseconds, bool completed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LocalCatalogEntry>> SearchAsync(string keyword, SearchResultFilter filter, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalCatalogEntry>>([]);
        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
    }
}
