using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Lyrics;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;
using CatalogTrack = Beans.Windows.Rebuild.Services.LocalMusic.LocalTrack;

namespace Beans.Windows.Rebuild.Tests;

public sealed class LyricsServiceTests
{
    [Fact]
    public void ParserReadsOffsetMultipleTimestampsAndTranslation()
    {
        var document = new LrcParser().Parse(
            "[ar:Artist]\n[offset:500]\n[00:01.20][00:02.00]Main\n[00:01.20]翻译",
            PlatformId.Local,
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(500), document.Offset);
        Assert.Equal(2, document.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), document.Lines[0].Timestamp);
        Assert.Equal("Main", document.Lines[0].Text);
        Assert.Equal("翻译", document.Lines[0].Translation);
        Assert.Equal(TimeSpan.FromSeconds(2), document.Lines[1].Timestamp);
    }

    [Fact]
    public void ParserIgnoresMalformedAndMetadataOnlyLinesAndDetectsInstrumental()
    {
        var document = new LrcParser().Parse(
            "[ti:Instrumental]\n[offset:not-a-number]\n[99:99]bad\n[00:00.00]纯音乐，请欣赏",
            PlatformId.Local,
            TestContext.Current.CancellationToken);

        var line = Assert.Single(document.Lines);
        Assert.True(document.IsInstrumental);
        Assert.Equal(TimeSpan.Zero, line.Timestamp);
    }

    [Fact]
    public async Task ServiceCachesNormalizedResultAndCanInvalidate()
    {
        var adapter = new CountingAdapter(PlatformId.QqMusic, LyricsResult.Loaded(
            new LyricDocument([new LyricLine(TimeSpan.Zero, "line")], false, TimeSpan.Zero, PlatformId.QqMusic)));
        var service = new LyricsService([adapter]);
        var request = new LyricsRequest(PlatformId.QqMusic, "song", "qq:track:song");

        var first = await service.GetLyricsAsync(request, TestContext.Current.CancellationToken);
        var cached = await service.GetLyricsAsync(request, TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);
        Assert.True(cached.IsFromCache);
        Assert.Equal(1, adapter.CallCount);

        service.Invalidate(request);
        await service.GetLyricsAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, adapter.CallCount);
    }

    [Fact]
    public async Task ServicePropagatesCancellationAndDoesNotCacheCancelledRequest()
    {
        var adapter = new DelayingAdapter(PlatformId.QqMusic);
        var service = new LyricsService([adapter]);
        var request = new LyricsRequest(PlatformId.QqMusic, "song", "qq:track:song");
        using var cancellation = new CancellationTokenSource();
        var pending = service.GetLyricsAsync(request, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
        Assert.Equal(0, adapter.CompletedCount);
    }

    [Fact]
    public async Task ServiceIsExplicitlyUnsupportedForAnUnregisteredPlatform()
    {
        var result = await new LyricsService([]).GetLyricsAsync(
            new LyricsRequest(PlatformId.KuGouMusic, "song", "kugou:track:song"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LyricsLoadState.Unsupported, result.State);
        Assert.Null(result.Document);
        Assert.DoesNotContain("http", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QqAdapterLoadsAnonymousLrcThroughSharedNetworkClient()
    {
        Uri? requestUri = null;
        Uri? referer = null;
        var hasCookie = false;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            referer = request.Headers.Referrer;
            hasCookie = request.Headers.Contains("Cookie");
            return Task.FromResult(FakeHttpMessageHandler.Json(
                """{"retcode":0,"code":0,"subcode":0,"lyric":"[00:01.00]A &amp; B"}"""));
        });
        using var factory = new SingleClientFactory("qq", handler);
        var adapter = new QqLyricsSourceAdapter(factory, new PlatformJsonSerializer(), new LrcParser());

        var result = await adapter.GetLyricsAsync(
            new LyricsRequest(PlatformId.QqMusic, "0039MnYb0qxYhV", "qq:track:0039MnYb0qxYhV"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(PlatformId.QqMusic, result.Document!.Source);
        Assert.Equal("A & B", Assert.Single(result.Document.Lines).Text);
        Assert.Equal("c.y.qq.com", requestUri!.Host);
        Assert.Equal("/lyric/fcgi-bin/fcg_query_lyric_new.fcg", requestUri.AbsolutePath);
        Assert.Contains("songmid=0039MnYb0qxYhV", requestUri.Query, StringComparison.Ordinal);
        Assert.Equal("https://y.qq.com/portal/player.html", referer!.AbsoluteUri);
        Assert.False(hasCookie);
    }

    [Fact]
    public async Task NetEaseAdapterLoadsOriginalAndTimestampMatchedTranslation()
    {
        Uri? requestUri = null;
        var hasCookie = false;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            hasCookie = request.Headers.Contains("Cookie");
            return Task.FromResult(FakeHttpMessageHandler.Json(
                """{"code":200,"lrc":{"lyric":"[offset:500]\n[00:01.00]Hello"},"tlyric":{"lyric":"[00:01.50]你好"}}"""));
        });
        using var factory = new SingleClientFactory("netease", handler);
        var adapter = new NetEaseLyricsSourceAdapter(factory, new PlatformJsonSerializer(), new LrcParser());

        var result = await adapter.GetLyricsAsync(
            new LyricsRequest(PlatformId.NetEaseMusic, "186016", "netease:track:186016"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.Document!.Offset);
        var line = Assert.Single(result.Document.Lines);
        Assert.Equal("Hello", line.Text);
        Assert.Equal("你好", line.Translation);
        Assert.Equal("music.163.com", requestUri!.Host);
        Assert.Equal("/api/song/lyric", requestUri.AbsolutePath);
        Assert.Contains("id=186016", requestUri.Query, StringComparison.Ordinal);
        Assert.False(hasCookie);
    }

    [Fact]
    public async Task OnlineAdaptersRejectInvalidNativeIdsWithoutSendingARequest()
    {
        var qqHandler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        var netEaseHandler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        using var qqFactory = new SingleClientFactory("qq", qqHandler);
        using var netEaseFactory = new SingleClientFactory("netease", netEaseHandler);

        var qq = await new QqLyricsSourceAdapter(qqFactory, new PlatformJsonSerializer(), new LrcParser())
            .GetLyricsAsync(new LyricsRequest(PlatformId.QqMusic, "../cookie?x=1", "qq:track:bad"),
                TestContext.Current.CancellationToken);
        var netEase = await new NetEaseLyricsSourceAdapter(netEaseFactory, new PlatformJsonSerializer(), new LrcParser())
            .GetLyricsAsync(new LyricsRequest(PlatformId.NetEaseMusic, "-1&cookie=x", "netease:track:bad"),
                TestContext.Current.CancellationToken);

        Assert.Equal(LyricsLoadState.Error, qq.State);
        Assert.Equal(LyricsLoadState.Error, netEase.State);
        Assert.Equal(0, qqHandler.SendCount);
        Assert.Equal(0, netEaseHandler.SendCount);
        Assert.DoesNotContain("cookie", qq.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QqAdapterRejectsStructurallyInvalidResponseAsSafeError()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        using var factory = new SingleClientFactory("qq", handler);
        var result = await new QqLyricsSourceAdapter(factory, new PlatformJsonSerializer(), new LrcParser())
            .GetLyricsAsync(
                new LyricsRequest(PlatformId.QqMusic, "0039MnYb0qxYhV", "qq:track:0039MnYb0qxYhV"),
                TestContext.Current.CancellationToken);

        Assert.Equal(LyricsLoadState.Error, result.State);
        Assert.Null(result.Document);
        Assert.DoesNotContain("c.y.qq.com", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetEaseNoLyricAndPureMusicStatesAreExplicit()
    {
        var noLyricsHandler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("""{"code":200,"nolyric":true}""")));
        using var noLyricsFactory = new SingleClientFactory("netease", noLyricsHandler);
        var noLyrics = await new NetEaseLyricsSourceAdapter(noLyricsFactory, new PlatformJsonSerializer(), new LrcParser())
            .GetLyricsAsync(new LyricsRequest(PlatformId.NetEaseMusic, "1", "netease:track:1"),
                TestContext.Current.CancellationToken);

        var instrumentalHandler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("""{"code":200,"pureMusic":true}""")));
        using var instrumentalFactory = new SingleClientFactory("netease", instrumentalHandler);
        var instrumental = await new NetEaseLyricsSourceAdapter(instrumentalFactory, new PlatformJsonSerializer(), new LrcParser())
            .GetLyricsAsync(new LyricsRequest(PlatformId.NetEaseMusic, "2", "netease:track:2"),
                TestContext.Current.CancellationToken);

        Assert.Equal(LyricsLoadState.NotFound, noLyrics.State);
        Assert.Equal(LyricsLoadState.NotFound, instrumental.State);
        Assert.Contains("纯音乐", instrumental.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlineCacheExpiresButLocalCacheRemainsUntilInvalidated()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var online = new CountingAdapter(PlatformId.QqMusic, LyricsResult.Loaded(
            new LyricDocument([new LyricLine(TimeSpan.Zero, "online")], false, TimeSpan.Zero, PlatformId.QqMusic)));
        var local = new CountingAdapter(PlatformId.Local, LyricsResult.Loaded(
            new LyricDocument([new LyricLine(TimeSpan.Zero, "local")], false, TimeSpan.Zero, PlatformId.Local)));
        var service = new LyricsService([online, local], clock);
        var onlineRequest = new LyricsRequest(PlatformId.QqMusic, "0039MnYb0qxYhV", "qq:track:0039MnYb0qxYhV");
        var localRequest = new LyricsRequest(PlatformId.Local, "local", "local:track:local");

        await service.GetLyricsAsync(onlineRequest, TestContext.Current.CancellationToken);
        await service.GetLyricsAsync(localRequest, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));
        var refreshed = await service.GetLyricsAsync(onlineRequest, TestContext.Current.CancellationToken);
        var cachedLocal = await service.GetLyricsAsync(localRequest, TestContext.Current.CancellationToken);

        Assert.False(refreshed.IsFromCache);
        Assert.True(cachedLocal.IsFromCache);
        Assert.Equal(2, online.CallCount);
        Assert.Equal(1, local.CallCount);
    }

    [Fact]
    public async Task LocalAdapterLoadsSameNameLrcAndMergesTranslation()
    {
        var root = Path.Combine(Path.GetTempPath(), "beans-lyrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var audio = Path.Combine(root, "Song.wav");
        var lrc = Path.Combine(root, "Song.lrc");
        await File.WriteAllTextAsync(lrc, "[00:01.00]Main\n[00:01.00]Translation", TestContext.Current.CancellationToken);
        try
        {
            var track = new CatalogTrack(
                "local:track:song", "folder", audio, audio, "Song.wav", "wav", 1,
                DateTime.UtcNow, "Song", "Artist", "Album", "Artist", null, null, null,
                TimeSpan.FromSeconds(2), null, null, "wav", "ms-appx:///Assets/Home/track-sunlight.jpg",
                null, LocalLyricSource.None, LocalMetadataState.Fallback, false, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, null, 0);
            var adapter = new LocalLyricsSourceAdapter(
                new StubCatalog(track), new LocalLrcFileResolver(), new LrcParser());
            var result = await adapter.GetLyricsAsync(
                new LyricsRequest(PlatformId.Local, "song", track.Id, audio), TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            var line = Assert.Single(result.Document!.Lines);
            Assert.Equal("Main", line.Text);
            Assert.Equal("Translation", line.Translation);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class CountingAdapter(PlatformId platform, LyricsResult result) : ILyricsSourceAdapter
    {
        public PlatformId Platform { get; } = platform;
        public int CallCount { get; private set; }
        public Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class DelayingAdapter(PlatformId platform) : ILyricsSourceAdapter
    {
        public PlatformId Platform { get; } = platform;
        public int CompletedCount { get; private set; }
        public async Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            CompletedCount++;
            return LyricsResult.NotFound();
        }
    }

    private sealed class SingleClientFactory : IPlatformHttpClientFactory
    {
        private readonly string _platform;
        private readonly PlatformHttpClient _client;

        public SingleClientFactory(string platform, HttpMessageHandler handler)
        {
            _platform = platform;
            _client = NetworkingTestFactory.Client(handler, platform);
        }

        public IPlatformHttpClient Get(string platformId) => platformId == _platform
            ? _client
            : throw new KeyNotFoundException(platformId);

        public void Dispose() => _client.Dispose();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private sealed class StubCatalog(CatalogTrack track) : ILocalMusicCatalog
    {
        public bool IsAvailable => true;
        public event EventHandler<LocalScanProgress>? ProgressChanged { add { } remove { } }
        public LocalScanProgress ScanState => new(LocalScanStatus.Idle, null, null, 0, 0, 0, 0, 0, 0, 0, "");
        public Task<CatalogTrack?> GetTrackAsync(string stableId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CatalogTrack?>(stableId == track.Id ? track : null);
        public Task<IReadOnlyList<CatalogTrack>> GetTracksAsync(bool includeMissing = false, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogTrack>>([track]);
        public Task<LocalMusicFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveFolderAsync(string pathOrId, bool removeIndex = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LocalMusicFolder>> GetFoldersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalMusicFolder>>([]);
        public Task<LocalScanSummary> ScanAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogTrack>> GetRecentlyAddedAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogTrack>>([]);
        public Task<IReadOnlyList<CatalogTrack>> GetRecentlyPlayedAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogTrack>>([]);
        public Task<int> RemoveMissingFilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task RecordPlaybackStartedAsync(string stableId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordPlaybackProgressAsync(string stableId, long playedMilliseconds, bool completed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LocalCatalogEntry>> SearchAsync(string keyword, SearchResultFilter filter, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocalCatalogEntry>>([]);
        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
    }
}
