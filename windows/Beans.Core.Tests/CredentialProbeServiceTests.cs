using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Beans.Core;
using Xunit;

namespace Beans.Core.Tests;

public sealed class CredentialProbeServiceTests
{
    private static readonly Dictionary<string, string> QqCookies = new()
    {
        ["uin"] = "12345678",
        ["qm_keyst"] = "test-key"
    };

    [Fact]
    public async Task QqVkeyRejectionIsPlaybackLimited()
    {
        using var http = new HttpClient(new RouteHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("profile_homepage")) return Json("{\"code\":0,\"data\":{\"nick\":\"user\"}}");
            if (url.Contains("client_search")) return Json("{\"data\":{\"song\":{\"list\":[{\"songname\":\"晴天\",\"songmid\":\"0039MnYb0qxYhV\",\"strMediaMid\":\"0039MnYb0qxYhV\",\"singer\":[{\"name\":\"周杰伦\"}] }]}}}");
            if (url.Contains("musicu.fcg")) return Json("{\"req_0\":{\"data\":{\"sip\":[],\"midurlinfo\":[{\"purl\":\"\",\"result\":104003}]}}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var outcome = await new PlatformMusicClient(http).ProbeAsync("qq", QqCookies, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.PlaybackLimited, outcome.Status);
        Assert.Equal(104003, outcome.VkeyCode);
        Assert.False(outcome.PlaybackReady);
    }

    [Fact]
    public async Task QqCdnProbeRequestsOnlyTwoBytes()
    {
        RangeHeaderValue? observedRange = null;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("profile_homepage")) return Json("{\"code\":0}");
            if (url.Contains("client_search")) return Json("{\"data\":{\"song\":{\"list\":[{\"songname\":\"晴天\",\"songmid\":\"song\",\"strMediaMid\":\"media\",\"singer\":[{\"name\":\"周杰伦\"}] }]}}}");
            if (url.Contains("musicu.fcg")) return Json("{\"req_0\":{\"data\":{\"sip\":[\"https://cdn.example/\"],\"midurlinfo\":[{\"purl\":\"probe.mp3\",\"result\":0}]}}}");
            observedRange = request.Headers.Range;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0x49, 0x44])
            };
        }));
        var outcome = await new PlatformMusicClient(http).ProbeAsync("qq", QqCookies, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.Valid, outcome.Status);
        Assert.Equal("会员播放可用", outcome.Membership);
        Assert.Equal(0, observedRange?.Ranges.Single().From);
        Assert.Equal(1, observedRange?.Ranges.Single().To);
    }

    [Fact]
    public async Task QqProfileRejectionIsInvalid()
    {
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var outcome = await new PlatformMusicClient(http).ProbeAsync("qq", QqCookies, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.Invalid, outcome.Status);
        Assert.False(outcome.LoginValid);
        Assert.Equal(403, outcome.HttpStatus);
    }

    [Fact]
    public async Task QqUnavailableSampleDoesNotInvalidateLogin()
    {
        using var http = new HttpClient(new RouteHandler(request => request.RequestUri!.AbsoluteUri.Contains("profile_homepage")
            ? Json("{\"code\":0}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var outcome = await new PlatformMusicClient(http).ProbeAsync("qq", QqCookies, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.Valid, outcome.Status);
        Assert.True(outcome.LoginValid);
        Assert.Null(outcome.PlaybackReady);
    }

    [Fact]
    public async Task QqLibraryMergesAllPlaylistGroupsAndDeduplicates()
    {
        using var http = new HttpClient(new RouteHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("profile_homepage"))
                return Json("""{"code":0,"data":{"nick":"user","disslist":[{"dissid":"11","dissname":"我喜欢","songnum":8,"logo":"https://example.test/11.jpg"}]}}""");
            if (url.Contains("profile_order_asset"))
                return Json("""{"code":0,"data":{"created":{"cdlist":[{"dissid":"11","dissname":"我喜欢","songnum":12},{"dissid":"22","dissname":"自建歌单","song_cnt":3}]},"favorite":{"disslist":[{"tid":"33","dirname":"收藏歌单","song_count":6}]}}}""");
            if (url.Contains("user_created_diss"))
                return Json("""{"code":0,"data":{"disslist":[{"dissid":"44","dissname":"独立接口歌单","songnum":9},{"dissid":"22","dissname":"自建歌单","songnum":3}]}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await new PlatformMusicClient(http).ValidateAndLoadAsync("qq", QqCookies, TestContext.Current.CancellationToken);

        Assert.Equal("user", result.Profile.Nickname);
        Assert.Equal(4, result.Playlists.Count);
        Assert.Equal(12, result.Playlists.Single(item => item.Id == 11).TrackCount);
        Assert.Equal(3, result.Playlists.Single(item => item.Id == 22).TrackCount);
        Assert.Contains(result.Playlists, item => item.Id == 33 && item.Name == "收藏歌单" && item.NativeKind == "tid");
        Assert.Contains(result.Playlists, item => item.Id == 44 && item.Name == "独立接口歌单" && item.NativeKind == "diss");
    }

    [Fact]
    public async Task QqPlaylistDetailsLoadTrackMetadata()
    {
        using var http = new HttpClient(new RouteHandler(_ => Json("""
            {"code":0,"cdlist":[{"songlist":[
              {"songmid":"song-1","songname":"第一首","albumname":"专辑一","albummid":"album-1","singer":[{"name":"歌手甲"},{"name":"歌手乙"}]}
            ]}]}
            """)));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 12, QqCookies, "diss", TestContext.Current.CancellationToken);

        var track = Assert.Single(tracks);
        Assert.Equal("song-1", track.Id);
        Assert.Equal("第一首", track.Title);
        Assert.Equal("歌手甲 / 歌手乙", track.Artist);
        Assert.Equal("专辑一", track.Album);
        Assert.Equal("qq", track.Source);
    }

    [Fact]
    public async Task QqDirectoryPlaylistUsesFavoriteFolderEndpoint()
    {
        var directoryCalled = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_musiclist_getmyfav"))
            {
                directoryCalled = true;
                return Json("""{"code":0,"data":{"songlist":[{"songmid":"folder-song","songname":"目录歌曲","albumname":"目录专辑","singer":[{"name":"目录歌手"}]}]}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 201, QqCookies, "dir", TestContext.Current.CancellationToken);

        Assert.True(directoryCalled);
        Assert.Equal("目录歌曲", Assert.Single(tracks).Title);
    }

    [Fact]
    public async Task QqDirectoryPlaylistSupportsWrappedLegacyTrackShape()
    {
        using var http = new HttpClient(new RouteHandler(_ => Json("""
            {"code":0,"data":{"songlist":[{"musicData":{
              "Fsong_mid":"legacy-song","Fsong_name":"旧版歌曲","Falbum_name":"旧版专辑",
              "Falbum_mid":"legacy-album","Fsinger_name":"旧版歌手"
            }}]}}
            """)));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 204, QqCookies, "dir", TestContext.Current.CancellationToken);

        var track = Assert.Single(tracks);
        Assert.Equal("legacy-song", track.Id);
        Assert.Equal("旧版歌曲", track.Title);
        Assert.Equal("旧版歌手", track.Artist);
        Assert.Equal("旧版专辑", track.Album);
    }

    [Fact]
    public async Task QqDirectoryPlaylistLoadsSongMidsFromMapKeys()
    {
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_musiclist_getmyfav"))
                return Json("""{"code":0,"data":{"songlist":[],"mapmid":{"folder-mid":1}}}""");
            if (request.RequestUri!.AbsoluteUri.Contains("musicu.fcg"))
                return Json("""{"code":0,"req_0":{"code":0,"data":{"track_info":{"mid":"folder-mid","name":"目录补全歌曲","album":{"name":"目录专辑"},"singer":[{"name":"目录歌手"}]}}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 205, QqCookies, "dir", TestContext.Current.CancellationToken);

        Assert.Equal("目录补全歌曲", Assert.Single(tracks).Title);
    }

    [Fact]
    public async Task QqKnownEmptyPlaylistDoesNotCallDirectoryEndpoint()
    {
        var directoryCalled = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_musiclist_getmyfav")) directoryCalled = true;
            return Json("""{"code":0,"cdlist":[{"songlist":[]}]}""");
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 202, QqCookies, "diss", TestContext.Current.CancellationToken);

        Assert.Empty(tracks);
        Assert.False(directoryCalled);
    }

    [Fact]
    public async Task QqNamedPlaylistFallsBackToDirectoryWhenSongListIsMissing()
    {
        var directoryCalled = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_musiclist_getmyfav"))
            {
                directoryCalled = true;
                return Json("""{"code":0,"data":{"songlist":[{"songmid":"favorite-song","songname":"收藏歌曲","albumname":"收藏专辑","singer":[{"name":"收藏歌手"}]}]}}""");
            }
            return Json("""{"code":0,"cdlist":[]}""");
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 203, QqCookies, "diss", TestContext.Current.CancellationToken);

        Assert.True(directoryCalled);
        Assert.Equal("收藏歌曲", Assert.Single(tracks).Title);
    }

    [Fact]
    public async Task QqFavoritePlaylistResolvesMapBeforeLoadingDetails()
    {
        var requestedMappedId = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("fcg_musiclist_getmyfav") && url.Contains("dirid=201"))
                return Json("""{"code":0,"map":998}""");
            if (url.Contains("fcg_ucc_getcdinfo") && url.Contains("disstid=998"))
            {
                requestedMappedId = true;
                return Json("""{"code":0,"cdlist":[{"songlist":[{"songmid":"liked-song","songname":"喜欢的歌","albumname":"喜欢专辑","singer":[{"name":"歌手"}]}]}]}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 123, QqCookies, "favorite", TestContext.Current.CancellationToken);

        Assert.True(requestedMappedId);
        Assert.Equal("喜欢的歌", Assert.Single(tracks).Title);
    }

    [Fact]
    public async Task QqFavoritePlaylistLoadsSongMidsFromMapKeys()
    {
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_musiclist_getmyfav"))
                return Json("""{"code":0,"mapmid":{"liked-mid":1}}""");
            if (request.RequestUri!.AbsoluteUri.Contains("musicu.fcg"))
                return Json("""{"code":0,"req_0":{"code":0,"data":{"track_info":{"mid":"liked-mid","name":"喜欢的歌","album":{"name":"喜欢专辑"},"singer":[{"name":"喜欢歌手"}]}}}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 201, QqCookies, "favorite", TestContext.Current.CancellationToken);

        Assert.Equal("喜欢的歌", Assert.Single(tracks).Title);
    }

    [Fact]
    public async Task QqPlaylistUsesMusicuFallbackAndUnwrapsTrackInfo()
    {
        var musicuCalled = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_ucc_getcdinfo"))
                return Json("""{"code":0,"cdlist":[]}""");
            if (request.RequestUri!.AbsoluteUri.Contains("musicu.fcg"))
            {
                musicuCalled = true;
                return Json("""{"code":0,"req_1":{"code":0,"data":{"songlist":[{"track_info":{"mid":"musicu-song","name":"兜底歌曲","album":{"name":"兜底专辑","mid":"album-mid"},"singer":[{"name":"兜底歌手"}]}}]}}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 204, QqCookies, "diss", TestContext.Current.CancellationToken);

        Assert.True(musicuCalled);
        var track = Assert.Single(tracks);
        Assert.Equal("兜底歌曲", track.Title);
        Assert.Equal("兜底歌手", track.Artist);
        Assert.Equal("兜底专辑", track.Album);
    }

    [Fact]
    public async Task QqLegacyPlaylistFallsBackWhenDetailShapeIsUnknown()
    {
        var directoryCalled = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("fcg_musiclist_getmyfav"))
            {
                directoryCalled = true;
                return Json("""{"code":0,"data":{"songlist":[]}}""");
            }
            return Json("""{"code":0,"cdlist":[]}""");
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("qq", 203, QqCookies, TestContext.Current.CancellationToken);

        Assert.Empty(tracks);
        Assert.True(directoryCalled);
    }

    [Fact]
    public async Task NeteasePlaylistDetailsLoadTrackMetadata()
    {
        var credentials = new Dictionary<string, string> { ["MUSIC_U"] = "test" };
        using var http = new HttpClient(new RouteHandler(_ => Json("""
            {"code":200,"playlist":{"tracks":[
              {"id":42,"name":"云端歌曲","ar":[{"name":"云歌手"}],"al":{"name":"云专辑","picUrl":"https://example.test/cover.jpg"}}
            ]}}
            """)));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("netease", 99, credentials, null, TestContext.Current.CancellationToken);

        var track = Assert.Single(tracks);
        Assert.Equal("42", track.Id);
        Assert.Equal("云端歌曲", track.Title);
        Assert.Equal("云歌手", track.Artist);
        Assert.Equal("云专辑", track.Album);
        Assert.Equal("netease", track.Source);
    }

    [Fact]
    public async Task NeteasePlaylistCompletesMissingTracksFromTrackIds()
    {
        var credentials = new Dictionary<string, string> { ["MUSIC_U"] = "test" };
        var detailCalled = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("playlist/detail"))
                return Json("""{"code":200,"playlist":{"tracks":[{"id":41,"name":"已有歌曲","ar":[{"name":"歌手甲"}],"al":{"name":"专辑甲"}}],"trackIds":[{"id":41},{"id":42}]}}""");
            if (request.RequestUri!.AbsoluteUri.Contains("song/detail"))
            {
                detailCalled = true;
                return Json("""{"code":200,"songs":[{"id":42,"name":"补全歌曲","ar":[{"name":"歌手乙"}],"al":{"name":"专辑乙"}}]}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var tracks = await new PlatformMusicClient(http).LoadPlaylistTracksAsync("netease", 99, credentials, null, TestContext.Current.CancellationToken);

        Assert.True(detailCalled);
        Assert.Equal(2, tracks.Count);
        Assert.Contains(tracks, item => item.Id == "42" && item.Title == "补全歌曲");
    }

    [Fact]
    public async Task QqPlaybackUsesMediaIdAndReturnsReachableCdnSource()
    {
        var usedMediaId = false;
        string? playbackCookie = null;
        Uri? playbackReferrer = null;
        string? playbackOrigin = null;
        var playbackAcceptsAudio = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            if (request.RequestUri!.Host == "u.y.qq.com")
            {
                usedMediaId = Uri.UnescapeDataString(request.RequestUri.Query).Contains("M800media-mid.mp3", StringComparison.Ordinal);
                return Json("""{"code":0,"req_0":{"code":0,"data":{"sip":["https://cdn.example.test/"],"midurlinfo":[{"purl":"audio.mp3","result":0}]}}}""");
            }
            if (request.Headers.Range is null)
            {
                playbackCookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null;
                playbackReferrer = request.Headers.Referrer;
                playbackOrigin = request.Headers.TryGetValues("Origin", out var origins) ? origins.Single() : null;
                playbackAcceptsAudio = request.Headers.Accept.Any(item => item.MediaType == "audio/*");
            }
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0x49, 0x44, 0x33])
            };
        }));
        var track = new PlatformPlaylistTrack("song-mid", "歌曲", "歌手", "专辑", null, "qq", "media-mid");
        var client = new PlatformMusicClient(http);

        var source = await client.ResolvePlaybackAsync(track, QqCookies, TestContext.Current.CancellationToken);
        await using var buffered = new MemoryStream();
        await client.DownloadPlaybackAsync(source, track.Source, QqCookies, buffered, TestContext.Current.CancellationToken);

        Assert.True(usedMediaId);
        Assert.Equal("M800", source.Quality);
        Assert.Equal("cdn.example.test", source.Uri.Host);
        Assert.Contains("uin=12345678", playbackCookie);
        Assert.Equal("https://y.qq.com/", playbackReferrer?.AbsoluteUri);
        Assert.Equal("https://y.qq.com", playbackOrigin);
        Assert.True(playbackAcceptsAudio);
        Assert.Equal(3, buffered.Length);
    }

    [Fact]
    public async Task NeteasePlaybackReturnsOfficialPlayerUrl()
    {
        var credentials = new Dictionary<string, string> { ["MUSIC_U"] = "test" };
        using var http = new HttpClient(new RouteHandler(_ => Json("""
            {"code":200,"data":[{"id":42,"url":"https://cdn.example.test/song.mp3","br":320000}]}
            """)));
        var track = new PlatformPlaylistTrack("42", "歌曲", "歌手", "专辑", null, "netease");

        var source = await new PlatformMusicClient(http).ResolvePlaybackAsync(track, credentials, TestContext.Current.CancellationToken);

        Assert.Equal("cdn.example.test", source.Uri.Host);
        Assert.Equal("320000", source.Quality);
    }

    [Fact]
    public async Task Netease301IsInvalidButOrdinaryAccountIsValid()
    {
        var credentials = new Dictionary<string, string> { ["MUSIC_U"] = "test" };
        using var invalidHttp = new HttpClient(new RouteHandler(_ => Json("{\"code\":301}")));
        var invalid = await new PlatformMusicClient(invalidHttp).ProbeAsync("netease", credentials, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.Invalid, invalid.Status);

        var calls = 0;
        using var validHttp = new HttpClient(new RouteHandler(_ => ++calls == 1
            ? Json("{\"code\":200,\"profile\":{\"userId\":1,\"nickname\":\"user\"}}")
            : Json("{\"code\":200,\"data\":{\"redVipLevel\":0}}")));
        var valid = await new PlatformMusicClient(validHttp).ProbeAsync("netease", credentials, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.Valid, valid.Status);
        Assert.Equal("普通账号", valid.Membership);
    }

    [Fact]
    public async Task AutomaticProbeUsesTwentyFourHourWindowAndMergesConcurrentRuns()
    {
        var calls = 0;
        using var http = new HttpClient(new RouteHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(30);
            return calls % 2 == 1
                ? Json("{\"code\":200,\"profile\":{\"userId\":1}}")
                : Json("{\"code\":200,\"data\":{\"redVipLevel\":1}}");
        }));
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var credentials = new Dictionary<string, string> { ["MUSIC_U"] = "test" };
        var service = new CredentialProbeService(new PlatformMusicClient(http), provider => provider == "netease" ? credentials : null, clock: clock);
        var first = service.RunAsync("netease", CredentialProbeMode.Manual, TestContext.Current.CancellationToken);
        var second = service.RunAsync("netease", CredentialProbeMode.Manual, TestContext.Current.CancellationToken);
        Assert.Same(first, second);
        var result = await first;
        Assert.Equal(clock.GetUtcNow().AddHours(24), result.NextCheckAt);
        Assert.False(service.IsDue("netease"));
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(service.IsDue("netease"));
    }

    [Fact]
    public async Task NetworkFailureRetriesAfterOneHour()
    {
        using var http = new HttpClient(new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var now = new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        var credentials = new Dictionary<string, string> { ["MUSIC_U"] = "test" };
        var service = new CredentialProbeService(new PlatformMusicClient(http), provider => provider == "netease" ? credentials : null, clock: clock);
        var result = await service.RunAsync("netease", CredentialProbeMode.Automatic, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialProbeStatus.NetworkError, result.Status);
        Assert.Equal(now.AddHours(1), result.NextCheckAt);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now = now.Add(interval);
    }
}
