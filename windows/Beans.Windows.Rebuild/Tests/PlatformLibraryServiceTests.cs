using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PlatformLibraryServiceTests
{
    [Fact]
    public async Task LoadAllAsync_IsolatesProviderFailure()
    {
        var qq = new FakeAdapter(PlatformId.QqMusic, Snapshot(PlatformId.QqMusic, "QQ 歌单"));
        var netEase = new FakeAdapter(PlatformId.NetEaseMusic, exception: new HttpRequestException());
        using var service = new PlatformLibraryService([qq, netEase]);

        var result = await service.LoadAllAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Single(result.Single(item => item.Platform == PlatformId.QqMusic).Playlists);
        Assert.Equal(
            PlatformLibraryState.Error,
            result.Single(item => item.Platform == PlatformId.NetEaseMusic).State);
    }

    [Fact]
    public async Task LoadAsync_UsesStaleCacheWhenRefreshFails()
    {
        var adapter = new FakeAdapter(PlatformId.QqMusic, Snapshot(PlatformId.QqMusic, "QQ 歌单"));
        using var service = new PlatformLibraryService([adapter]);
        var initial = await service.LoadAsync(PlatformId.QqMusic, false, TestContext.Current.CancellationToken);
        adapter.Exception = new HttpRequestException();

        var refreshed = await service.LoadAsync(PlatformId.QqMusic, true, TestContext.Current.CancellationToken);

        Assert.Single(initial.Playlists);
        Assert.Single(refreshed.Playlists);
        Assert.Equal(PlatformLibraryState.Partial, refreshed.State);
        Assert.Equal(SearchDataOrigin.CacheStale, refreshed.DataOrigin);
        Assert.True(refreshed.IsPartialSuccess);
    }

    private static PlatformLibrarySnapshot Snapshot(PlatformId platform, string title) => new(
        platform,
        new PlatformProbeSnapshot(
            platform,
            CredentialState.Valid,
            "用户",
            "42",
            PlatformMembershipState.Unknown,
            "会员状态未知",
            DateTimeOffset.UtcNow,
            "探针有效"),
        [new PlatformUserPlaylist(platform, "100", "playlist", title, "用户", "", 3)],
        PlatformLibraryState.Succeeded,
        SearchDataOrigin.Live,
        DateTimeOffset.UtcNow,
        "已加载");

    private sealed class FakeAdapter(
        PlatformId platform,
        PlatformLibrarySnapshot? snapshot = null,
        Exception? exception = null) : IPlatformLibraryAdapter
    {
        public PlatformId Platform { get; } = platform;
        public Exception? Exception { get; set; } = exception;

        public Task<PlatformLibrarySnapshot> LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Exception is null
                ? Task.FromResult(snapshot!)
                : Task.FromException<PlatformLibrarySnapshot>(Exception);
        }

        public Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
            PlatformUserPlaylist playlist,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlatformPlaylistTrack>>([]);
    }
}
