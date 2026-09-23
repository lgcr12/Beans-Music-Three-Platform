using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Pages.Artist;
using Beans.Windows.Rebuild.Pages.Library;
using Beans.Windows.Rebuild.Pages.Shared;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PageTrackPlaybackMappingTests
{
    [Fact]
    public void LiveQqDetailTrackPreservesPlatformIdentityAndMetadata()
    {
        var track = new DetailTrackPreview(
            "0039MnYb0qxYhV", "一路向北", "周杰伦", "JAY", "04:55", PlatformId.QqMusic, false,
            "需要平台授权", SearchDataOrigin.Live, "https://example.invalid/cover.jpg", "FLAC");

        Assert.True(track.TryCreateSearchResult(out var item));
        Assert.NotNull(item);
        Assert.Equal(SearchResultType.Track, item.ResultType);
        Assert.Equal(PlatformId.QqMusic, item.Platform);
        Assert.Equal("0039MnYb0qxYhV", item.NativeId);
        Assert.Equal("qq:track:0039MnYb0qxYhV", item.StableId);
        Assert.Equal("周杰伦", item.Artist);
        Assert.Equal("JAY", item.Album);
        Assert.Equal(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(55), item.Duration);
        Assert.Equal(SearchDataOrigin.Live, item.DataOrigin);
        Assert.Null(item.PlaybackUri);
    }

    [Fact]
    public void CachedNetEaseLibraryTrackCanUseExistingResolverBoundary()
    {
        var track = new LibraryTrackPreview(
            "1974443814", "一格格", "卫兰", "DAUGHTER", "04:05", "", PlatformId.NetEaseMusic, false,
            "需要平台授权", SearchDataOrigin.CacheStale, "320K");

        Assert.True(track.TryCreateSearchResult(out var item));
        Assert.NotNull(item);
        Assert.Equal(PlatformId.NetEaseMusic, item.Platform);
        Assert.Equal("1974443814", item.NativeId);
        Assert.Equal("netease:track:1974443814", item.PayloadReference);
        Assert.Equal(SearchDataOrigin.CacheStale, item.DataOrigin);
        Assert.False(item.IsPlayable);
        Assert.Null(item.PlaybackUri);
    }

    [Theory]
    [InlineData(PlatformId.QqMusic, "0039MnYb0qxYhV")]
    [InlineData(PlatformId.NetEaseMusic, "1974443814")]
    public void PreviewOriginIsNeverConvertedEvenWithPlausibleNativeId(PlatformId platform, string nativeId)
    {
        var converted = OnlinePageTrackMapper.TryCreateSearchResult(
            platform, nativeId, "Preview", "Artist", "Album", TimeSpan.FromMinutes(3), "", "未知",
            SearchDataOrigin.Preview, out var item);

        Assert.False(converted);
        Assert.Null(item);
    }

    [Theory]
    [InlineData(PlatformId.QqMusic, "preview-track-1")]
    [InlineData(PlatformId.QqMusic, "qq-north")]
    [InlineData(PlatformId.NetEaseMusic, "netease-letting-go")]
    [InlineData(PlatformId.NetEaseMusic, "0")]
    [InlineData(PlatformId.Local, "C:\\Music\\song.mp3")]
    [InlineData(PlatformId.Beans, "42")]
    public void SyntheticOrUnsupportedIdentityIsRejected(PlatformId platform, string nativeId)
    {
        Assert.False(OnlinePageTrackMapper.TryCreateSearchResult(
            platform, nativeId, "Track", "Artist", "Album", null, "", "未知", SearchDataOrigin.Live, out var item));
        Assert.Null(item);
    }

    [Fact]
    public void BundledPreviewCollectionsCannotEnterOnlinePlayback()
    {
        Assert.All(DetailPreviewData.Tracks(PlatformId.QqMusic, "歌手"), track => Assert.False(track.CanAttemptPlayback));
        Assert.All(DetailPreviewData.Tracks(PlatformId.NetEaseMusic, "歌手"), track => Assert.False(track.CanAttemptPlayback));
        Assert.All(LibraryPreviewData.RecentTracks, track => Assert.False(track.CanAttemptPlayback));
        Assert.All(LibraryPreviewData.Playlists, playlist => Assert.False(LibraryPreviewData.GetPlaylist(playlist.Id).CanAttemptPlayback));
    }
}
