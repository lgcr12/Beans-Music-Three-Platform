using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PlaybackIntegrationTests
{
    [Fact]
    public async Task RouterDispatchesByPlatformAndPreservesUnauthorizedBoundary()
    {
        var qq = new FakeResolver(PlatformId.QqMusic,
            PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "请先登录 QQ 音乐"));
        var netease = new FakeResolver(PlatformId.NetEaseMusic,
            new PlaybackSourceResult(true, new PlaybackSource(new Uri("https://audio.example/track.mp3"), AudioQuality.High, null),
                PlaybackRestriction.None, "播放源已准备"));
        var router = new PlaybackSourceResolverRouter([qq, netease]);

        var denied = await router.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "qq-track")), CancellationToken.None);
        var allowed = await router.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.NetEaseMusic, "123")), CancellationToken.None);

        Assert.False(denied.IsSuccess);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, denied.Restriction);
        Assert.Equal(1, qq.CallCount);
        Assert.True(allowed.IsSuccess);
        Assert.Equal(1, netease.CallCount);
    }

    [Fact]
    public async Task RouterReturnsSafeUnsupportedBoundaryForUnregisteredPlatform()
    {
        var router = new PlaybackSourceResolverRouter([
            new FakeResolver(PlatformId.QqMusic,
                PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "请先登录 QQ 音乐"))]);

        var result = await router.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.Beans, "beans-track")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackRestriction.NotImplemented, result.Restriction);
        Assert.Null(result.Source);
    }

    [Fact]
    public void SuccessfulOnlineSourceConvertsToExistingPlaybackItem()
    {
        var item = new SearchResultItem(
            SearchResultType.Track,
            PlatformId.NetEaseMusic,
            "123",
            "netease:track:123",
            "测试歌曲",
            Artist: "测试歌手",
            Duration: TimeSpan.FromMinutes(3),
            SourceDisplayName: "网易云音乐",
            DataOrigin: SearchDataOrigin.Live);
        var source = new PlaybackSourceResult(
            true,
            new PlaybackSource(new Uri("https://audio.example/track.mp3"), AudioQuality.VeryHigh, null),
            PlaybackRestriction.None,
            "播放源已准备");

        var result = PlaybackItemFactory.Create(item, source);

        Assert.True(result.IsSuccess);
        Assert.Equal("netease:track:123", result.Item!.Id);
        Assert.Equal("https://audio.example/track.mp3", result.Item.SourceUri);
        Assert.Equal("SQ", result.Item.QualityLabel);
        Assert.Equal("网易云音乐", result.Item.SourceLabel);
        Assert.Equal(PlatformId.NetEaseMusic, result.Item.Platform);
        Assert.Equal("123", result.Item.NativeId);
        Assert.Equal(SearchDataOrigin.Live, result.Item.DataOrigin);
    }

    [Fact]
    public void UnauthorizedSourceDoesNotCreatePlaybackItem()
    {
        var item = new SearchResultItem(
            SearchResultType.Track, PlatformId.QqMusic, "mid", "qq:track:mid", "歌曲",
            DataOrigin: SearchDataOrigin.Live);
        var source = PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, "请先登录 QQ 音乐");

        var result = PlaybackItemFactory.Create(item, source);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Item);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, result.Restriction);
        Assert.Equal("请先登录 QQ 音乐", result.SafeMessage);
    }

    [Fact]
    public void PreviewTrackCannotEnterPlaybackEvenWithResolvedSource()
    {
        var item = new SearchResultItem(
            SearchResultType.Track,
            PlatformId.QqMusic,
            "preview-id",
            "qq:track:preview-id",
            "预览歌曲",
            DataOrigin: SearchDataOrigin.Preview);
        var source = new PlaybackSourceResult(
            true,
            new PlaybackSource(new Uri("https://audio.example/preview.mp3"), AudioQuality.High, null),
            PlaybackRestriction.None,
            "播放源已准备");

        var result = PlaybackItemFactory.Create(item, source);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Item);
        Assert.Equal(PlaybackRestriction.NotImplemented, result.Restriction);
    }

    private sealed class FakeResolver(PlatformId platform, PlaybackSourceResult result) : IPlaybackSourceResolver
    {
        public PlatformId Platform { get; } = platform;
        public int CallCount { get; private set; }

        public Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
