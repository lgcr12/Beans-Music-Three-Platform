using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Services.Search;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqSearchAdapterTests
{
    [Fact]
    public void DependencyInjectionResolvesTheRealQqSearchAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPlatformHttpClientFactory>(
            QqDiscoveryTestInfrastructure.Factory(QqSearchTestInfrastructure.RoutingHandler()));
        services.AddSingleton<IPlatformJsonSerializer, PlatformJsonSerializer>();
        services.AddSingleton<IPlatformErrorMapper, PlatformErrorMapper>();
        services.AddSingleton<IDiscoveryCache, DiscoveryCache>();
        services.AddSingleton<IPlatformSearchAdapter, QqMusicSearchAdapter>();
        using var provider = services.BuildServiceProvider();

        var adapter = provider.GetRequiredService<IPlatformSearchAdapter>();

        Assert.IsType<QqMusicSearchAdapter>(adapter);
        Assert.Equal(PlatformId.QqMusic, adapter.Platform);
    }

    [Fact]
    public void CapabilitiesMatchTheAnonymousReferenceBoundary()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(QqSearchTestInfrastructure.RoutingHandler());
        var capabilities = QqSearchTestInfrastructure.Adapter(factory).Capabilities;

        Assert.True(capabilities.SupportsAnonymousTracks);
        Assert.True(capabilities.SupportsAnonymousAlbums);
        Assert.True(capabilities.SupportsAnonymousArtists);
        Assert.True(capabilities.SupportsAnonymousSuggestions);
        Assert.False(capabilities.SupportsAnonymousPlaylists);
    }

    [Fact]
    public async Task TrackSearchUsesAnonymousClientSearchRequestAndMapsRealFields()
    {
        Uri? requestUri = null;
        Uri? referer = null;
        bool hadCookie = false;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            referer = request.Headers.Referrer;
            hadCookie = request.Headers.Contains("Cookie");
            return Task.FromResult(FakeHttpMessageHandler.Json(Fixture("qq-search-tracks-success.json")));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal("/soso/fcgi-bin/search_for_qq_cp", requestUri!.AbsolutePath);
        Assert.Contains("w=%E5%91%A8%E6%9D%B0%E4%BC%A6", requestUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("n=30", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("p=1", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("t=0", requestUri.Query, StringComparison.Ordinal);
        Assert.Equal("https://y.qq.com/portal/player.html", referer!.AbsoluteUri);
        Assert.False(hadCookie);
        Assert.Equal(SearchSourceState.Succeeded, response.State);
        Assert.Equal(SearchDataOrigin.Live, response.DataOrigin);
        Assert.Equal(42, response.TotalCount);
        Assert.True(response.HasMore);
        var track = response.Items[0];
        Assert.Equal(PlatformId.QqMusic, track.Platform);
        Assert.Equal("TRACKMID001", track.NativeId);
        Assert.Equal("qq:track:TRACKMID001", track.StableId);
        Assert.Equal("晴天", track.Title);
        Assert.Equal("周杰伦", track.Artist);
        Assert.Equal("叶惠美", track.Album);
        Assert.Equal(TimeSpan.FromSeconds(269), track.Duration);
        Assert.Equal("320K", track.Quality);
        Assert.Equal("QQ 音乐", track.SourceDisplayName);
        Assert.False(track.IsPlayable);
        Assert.Equal("播放时需要登录并解析 QQ 音乐官方播放地址", track.RestrictionState);
    }

    [Fact]
    public async Task UnifiedSearchPreservesQqReportedTotalCount()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(QqSearchTestInfrastructure.RoutingHandler());
        var service = new MusicSearchService([Adapter(factory)]);

        var response = await service.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(42, response.TotalCount);
        Assert.Equal(SearchDataOrigin.Live, response.DataOrigin);
    }

    [Fact]
    public async Task AlbumSearchMapsMidCoverArtistAndYear()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(QqSearchTestInfrastructure.RoutingHandler());

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Albums), TestContext.Current.CancellationToken);

        var album = Assert.Single(response.Items);
        Assert.Equal(SearchResultType.Album, album.ResultType);
        Assert.Equal("ALBUMMID001", album.NativeId);
        Assert.Equal("qq:album:ALBUMMID001", album.StableId);
        Assert.Equal("叶惠美", album.Title);
        Assert.Contains("周杰伦", album.Subtitle);
        Assert.Contains("2003-07-31", album.Subtitle);
        Assert.Contains("T002R300x300M000ALBUMMID001", album.CoverUri);
        Assert.Equal(7, response.TotalCount);
    }

    [Fact]
    public async Task ArtistSearchAndSuggestionsUseAnonymousSmartbox()
    {
        var handler = QqSearchTestInfrastructure.RoutingHandler();
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);

        var response = await adapter.SearchAsync(Query(SearchResultFilter.Artists), TestContext.Current.CancellationToken);
        var suggestions = await adapter.GetSuggestionsAsync("周杰伦", TestContext.Current.CancellationToken);

        var artist = Assert.Single(response.Items);
        Assert.Equal(SearchResultType.Artist, artist.ResultType);
        Assert.Equal("ARTISTMID001", artist.NativeId);
        Assert.Equal("qq:artist:ARTISTMID001", artist.StableId);
        Assert.Contains("T001R300x300M000ARTISTMID001", artist.CoverUri);
        Assert.Equal(3, suggestions.Count);
        Assert.All(suggestions, suggestion => Assert.Equal(PlatformId.QqMusic, suggestion.Platform));
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task AllSearchRunsOnlyThreeSupportedFamiliesAndAggregatesResults()
    {
        var handler = QqSearchTestInfrastructure.RoutingHandler();
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.All), TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.SendCount);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Track);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Album);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Artist);
        Assert.DoesNotContain(response.Items, item => item.ResultType == SearchResultType.Playlist);
        Assert.False(response.IsPartialSuccess);
    }

    [Fact]
    public async Task AllSearchKeepsSuccessfulFamiliesWhenAlbumRequestFails()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("t=8", StringComparison.Ordinal))
                return Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
            var fixture = request.RequestUri.AbsolutePath.Contains("smartbox", StringComparison.Ordinal)
                ? "qq-search-smartbox-success.json"
                : "qq-search-tracks-success.json";
            return Task.FromResult(FakeHttpMessageHandler.Json(Fixture(fixture)));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.All), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Succeeded, response.State);
        Assert.True(response.IsPartialSuccess);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Track);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Artist);
        Assert.DoesNotContain(response.Items, item => item.ResultType == SearchResultType.Album);
    }

    [Fact]
    public async Task PlaylistSearchIsUnsupportedAndDoesNotEnterNetwork()
    {
        var handler = QqSearchTestInfrastructure.RoutingHandler();
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Playlists), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Unsupported, response.State);
        Assert.Empty(response.Items);
        Assert.Contains("未提供", response.SafeMessage);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task EmptyOrControlOnlyKeywordDoesNotEnterNetwork()
    {
        var handler = QqSearchTestInfrastructure.RoutingHandler();
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(
            new SearchQuery(" \r\n\t ", SearchResultFilter.Tracks, SearchSourceScope.Qq),
            TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Empty, response.State);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task OffsetMapsToStablePageAndCacheKeyIncludesPaging()
    {
        Uri? requestUri = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            return Task.FromResult(FakeHttpMessageHandler.Json(Fixture("qq-search-tracks-success.json")));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        await Adapter(factory).SearchAsync(
            new SearchQuery(" 周杰伦 ", SearchResultFilter.Tracks, SearchSourceScope.Qq, Offset: 30, PageSize: 30),
            TestContext.Current.CancellationToken);

        Assert.Contains("p=2", requestUri!.Query, StringComparison.Ordinal);
        var key = QqSearchRequestKeys.Search(SearchResultFilter.Tracks, "周杰伦", 2, 30);
        Assert.Equal("qq:search:tracks:周杰伦:2:30", key);
        RequestKeyValidator.Validate("qq", key);
        Assert.NotEqual(key, QqSearchRequestKeys.Search(SearchResultFilter.Albums, "周杰伦", 2, 30));
        Assert.NotEqual(key, QqSearchRequestKeys.Search(SearchResultFilter.Tracks, "周杰伦", 1, 30));
    }

    [Fact]
    public void UnsafeKeywordIsRepresentedByDeterministicNonSensitiveRequestKey()
    {
        var first = QqSearchRequestKeys.Search(SearchResultFilter.Tracks, "token=private", 1, 30);
        var second = QqSearchRequestKeys.Search(SearchResultFilter.Tracks, "token=private", 1, 30);

        Assert.Equal(first, second);
        Assert.DoesNotContain("token", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", first, StringComparison.OrdinalIgnoreCase);
        RequestKeyValidator.Validate("qq", first);
    }

    [Fact]
    public async Task FreshCacheAvoidsSecondNetworkRequestAndMarksItems()
    {
        var handler = QqSearchTestInfrastructure.RoutingHandler();
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);
        await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        var cached = await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.SendCount);
        Assert.Equal(SearchDataOrigin.CacheFresh, cached.DataOrigin);
        Assert.All(cached.Items, item => Assert.Equal(SearchDataOrigin.CacheFresh, item.DataOrigin));
    }

    [Fact]
    public async Task FailedForceRefreshKeepsSuccessfulFreshCache()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => Task.FromResult(FakeHttpMessageHandler.Json(Fixture("qq-search-tracks-success.json"))),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);
        var first = await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        var fallback = await adapter.SearchAsync(Query(SearchResultFilter.Tracks, forceRefresh: true), TestContext.Current.CancellationToken);

        Assert.Equal(SearchDataOrigin.Live, first.DataOrigin);
        Assert.Equal(SearchDataOrigin.CacheFresh, fallback.DataOrigin);
        Assert.Equal(first.Items[0].NativeId, fallback.Items[0].NativeId);
    }

    [Fact]
    public async Task NetworkFailureUsesExpiredSuccessfulCacheAsStale()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => Task.FromResult(FakeHttpMessageHandler.Json(Fixture("qq-search-tracks-success.json"))),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = QqSearchTestInfrastructure.Adapter(factory, new DiscoveryCache(), TimeSpan.FromMilliseconds(1));
        await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);
        var cachedBefore = DateTimeOffset.UtcNow;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        var fallback = await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(SearchDataOrigin.CacheStale, fallback.DataOrigin);
        Assert.True(fallback.LoadedAt <= cachedBefore);
        Assert.All(fallback.Items, item => Assert.Equal(SearchDataOrigin.CacheStale, item.DataOrigin));
        Assert.Contains("上次搜索结果", fallback.SafeMessage);
    }

    [Fact]
    public async Task SameInFlightSearchIsMergedBySharedPlatformClient()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(50), Fixture("qq-search-tracks-success.json"));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);

        await Task.WhenAll(
            adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken),
            adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task CancelledSearchDoesNotPopulateCache()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(80), Fixture("qq-search-tracks-success.json"));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);
        using var cancellation = new CancellationTokenSource();

        var pending = adapter.SearchAsync(Query(SearchResultFilter.Tracks), cancellation.Token);
        await handler.Started.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.SendCount);
    }

    [Theory]
    [InlineData("qq-invalid-json.json", PlatformErrorCode.ParseFailure)]
    [InlineData("qq-search-missing-required.json", PlatformErrorCode.InvalidResponse)]
    [InlineData("qq-search-business-error.json", PlatformErrorCode.ServiceUnavailable)]
    public async Task InvalidPayloadsReturnSafeSourceError(string fixture, PlatformErrorCode expectedCode)
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json(Fixture(fixture))));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Error, response.State);
        Assert.Equal(expectedCode, response.ErrorCode);
        Assert.Empty(response.Items);
        Assert.Equal("QQ 音乐搜索暂时不可用", response.SafeMessage);
    }

    [Fact]
    public async Task EmptyResultIsSuccessfulAndNotPreview()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json(Fixture("qq-search-empty.json"))));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Empty, response.State);
        Assert.Equal(SearchDataOrigin.Live, response.DataOrigin);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task SafeLogContainsOnlyHostAndPathForSearchQuery()
    {
        var logger = new CaptureSafeLogger();
        using var factory = QqDiscoveryTestInfrastructure.Factory(QqSearchTestInfrastructure.RoutingHandler(), logger);

        await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Started);
        Assert.Equal("c.y.qq.com", entry.Host);
        Assert.Equal("/soso/fcgi-bin/search_for_qq_cp", entry.Path);
        Assert.DoesNotContain("周杰伦", entry.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("?", entry.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizedSearchFixturesContainNoCredentialOrPlaybackMaterial()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "QQ");
        foreach (var path in Directory.EnumerateFiles(directory, "qq-search-*.json"))
        {
            var text = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ToLowerInvariant();
            Assert.DoesNotContain("cookie", text);
            Assert.DoesNotContain("token", text);
            Assert.DoesNotContain("authorization", text);
            Assert.DoesNotContain("play_url", text);
        }
    }

    private static QqMusicSearchAdapter Adapter(IPlatformHttpClientFactory factory) =>
        QqSearchTestInfrastructure.Adapter(factory);

    private static SearchQuery Query(SearchResultFilter filter, bool forceRefresh = false) =>
        new("周杰伦", filter, SearchSourceScope.Qq, ForceRefresh: forceRefresh);

    private static string Fixture(string name) => QqSearchTestInfrastructure.Fixture(name);
}
