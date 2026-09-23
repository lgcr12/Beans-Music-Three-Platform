using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Details;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class OnlineMusicDetailTests
{
    [Fact]
    public async Task QqPlaylist_MapsRealNativeTrackIds()
    {
        const string json = """
        {"code":0,"cdlist":[{"disstid":"880001","dissname":"公开歌单","desc":"真实描述","logo":"https://img.test/cover.jpg","creator":{"name":"创建者"},"songlist":[{"songmid":"003abcXYZ","songname":"歌曲甲","singer":[{"mid":"s1","name":"歌手甲"}],"albummid":"album1","albumname":"专辑甲","interval":245,"file":{"size_flac":1200}}]}]}
        """;
        Uri? requestUri = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(FakeHttpMessageHandler.Json(json));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = new QqMusicDetailAdapter(factory, new PlatformJsonSerializer());

        var response = await adapter.GetDetailAsync(new OnlineMusicDetailQuery(
            PlatformId.QqMusic, OnlineMusicDetailKind.Playlist, "880001", "fallback"), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("公开歌单", response.Content!.Title);
        var track = Assert.Single(response.Content.Tracks);
        Assert.Equal("003abcXYZ", track.NativeId);
        Assert.Equal(SearchDataOrigin.Live, track.DataOrigin);
        Assert.Equal("FLAC", track.Quality);
        Assert.Equal("c.y.qq.com", requestUri!.Host);
        Assert.Contains("disstid=880001", requestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QqAlbum_IsExplicitlyUnsupported_WithoutNetworkCall()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("Network must not be called"));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = new QqMusicDetailAdapter(factory, new PlatformJsonSerializer());

        var response = await adapter.GetDetailAsync(new OnlineMusicDetailQuery(
            PlatformId.QqMusic, OnlineMusicDetailKind.Album, "001AlbumMid", "album"), TestContext.Current.CancellationToken);

        Assert.Equal(OnlineMusicDetailState.Unsupported, response.State);
        Assert.Equal(PlatformErrorCode.Unsupported, response.ErrorCode);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task QqArtist_UsesCurrentMusicuSongListShape()
    {
        const string json = """
        {"code":0,"singerSongList":{"code":0,"data":{"singerMid":"000SingerMid","songList":[{"songInfo":{"mid":"003SongMid","name":"热门歌曲","singer":[{"mid":"000SingerMid","name":"歌手甲"}],"album":{"mid":"001AlbumMid","name":"热门专辑"},"interval":180,"file":{"size_320mp3":1000}}}]}}}
        """;
        HttpMethod? method = null;
        Uri? requestUri = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            method = request.Method;
            requestUri = request.RequestUri;
            return Task.FromResult(FakeHttpMessageHandler.Json(json));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = new QqMusicDetailAdapter(factory, new PlatformJsonSerializer());

        var response = await adapter.GetDetailAsync(new OnlineMusicDetailQuery(
            PlatformId.QqMusic, OnlineMusicDetailKind.Artist, "000SingerMid", "歌手甲"), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("歌手甲", response.Content!.Title);
        Assert.Equal("003SongMid", Assert.Single(response.Content.Tracks).NativeId);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("u.y.qq.com", requestUri!.Host);
        Assert.Equal("/cgi-bin/musicu.fcg", requestUri.AbsolutePath);
    }

    [Fact]
    public async Task NetEaseArtist_MapsPublicHotSongs()
    {
        const string json = """
        {"code":200,"artist":{"id":6452,"name":"歌手乙","briefDesc":"公开资料","picUrl":"http://img.test/artist.jpg"},"hotSongs":[{"id":190001,"name":"歌曲乙","ar":[{"id":6452,"name":"歌手乙"}],"al":{"id":99,"name":"专辑乙","picUrl":"https://img.test/album.jpg"},"dt":201000,"h":{"br":320000}}]}
        """;
        HttpMethod? method = null;
        Uri? requestUri = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            method = request.Method;
            requestUri = request.RequestUri;
            return Task.FromResult(FakeHttpMessageHandler.Json(json));
        });
        using var factory = NetEaseFactory(handler);
        var adapter = new NetEaseMusicDetailAdapter(factory, new PlatformJsonSerializer());

        var response = await adapter.GetDetailAsync(new OnlineMusicDetailQuery(
            PlatformId.NetEaseMusic, OnlineMusicDetailKind.Artist, "6452", "fallback"), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("歌手乙", response.Content!.Title);
        var track = Assert.Single(response.Content.Tracks);
        Assert.Equal("190001", track.NativeId);
        Assert.Equal("320K", track.Quality);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("music.163.com", requestUri!.Host);
        Assert.Equal("/weapi/v1/artist/6452", requestUri.AbsolutePath);
    }

    [Theory]
    [InlineData("qq", "playlist", "abc")]
    [InlineData("qq", "artist", "bad/id")]
    [InlineData("netease", "album", "-1")]
    [InlineData("netease", "ranking", "0")]
    public async Task Service_RejectsInvalidNativeIds_BeforeAdapter(string platformValue, string kindValue, string nativeId)
    {
        Assert.True(PlatformIdExtensions.TryParseStableId(platformValue, out var platform));
        Assert.True(Enum.TryParse<OnlineMusicDetailKind>(kindValue, true, out var kind));
        var adapter = new ScriptedDetailAdapter(platform);
        var service = new OnlineMusicDetailService([adapter], new DiscoveryCache());

        var response = await service.GetDetailAsync(new OnlineMusicDetailQuery(platform, kind, nativeId), TestContext.Current.CancellationToken);

        Assert.Equal(OnlineMusicDetailState.InvalidRequest, response.State);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task Service_ReturnsCacheFresh_AndRewritesTrackOrigin()
    {
        var adapter = new ScriptedDetailAdapter(PlatformId.NetEaseMusic);
        var service = new OnlineMusicDetailService([adapter], new DiscoveryCache());
        var query = new OnlineMusicDetailQuery(PlatformId.NetEaseMusic, OnlineMusicDetailKind.Playlist, "123");

        var first = await service.GetDetailAsync(query, TestContext.Current.CancellationToken);
        var second = await service.GetDetailAsync(query, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, adapter.CallCount);
        Assert.Equal(SearchDataOrigin.CacheFresh, second.Content!.DataOrigin);
        Assert.All(second.Content.Tracks, track => Assert.Equal(SearchDataOrigin.CacheFresh, track.DataOrigin));
    }

    private static PlatformHttpClientFactory NetEaseFactory(HttpMessageHandler netEaseHandler) => new(
        new Dictionary<string, HttpMessageHandler>
        {
            ["qq"] = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{}"))),
            ["netease"] = netEaseHandler,
            ["kugou"] = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json("{}")))
        }, new CaptureSafeLogger(), new SensitiveDataRedactor(), new PlatformErrorMapper());

    private sealed class ScriptedDetailAdapter(PlatformId platform) : IOnlineMusicDetailAdapter
    {
        public PlatformId Platform { get; } = platform;
        public int CallCount { get; private set; }
        public bool Supports(OnlineMusicDetailKind kind) => true;
        public Task<OnlineMusicDetailResponse> GetDetailAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken)
        {
            CallCount++;
            var track = new SearchResultItem(SearchResultType.Track, Platform, "99", $"{Platform.ToStableId()}:track:99", "Track",
                Artist: "Artist", DataOrigin: SearchDataOrigin.Live, SourceDisplayName: Platform.ToDisplayName());
            var content = new OnlineMusicDetailContent(Platform, query.Kind, query.NativeId, "Title", "Subtitle", "Description",
                "ms-appx:///Assets/Branding/beans-icon.png", [track], [], SearchDataOrigin.Live, DateTimeOffset.UtcNow);
            return Task.FromResult(OnlineMusicDetailResponse.Success(content));
        }
    }
}
