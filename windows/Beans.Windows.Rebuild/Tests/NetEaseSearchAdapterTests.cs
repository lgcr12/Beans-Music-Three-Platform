using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Services.Search;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEaseSearchAdapterTests
{
    [Fact]
    public void DependencyInjectionResolvesTheRealNetEaseSearchAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPlatformHttpClientFactory>(
            NetEaseDiscoveryTestInfrastructure.Factory(NetEaseSearchTestInfrastructure.Handler("netease-search-tracks-success.json")));
        services.AddSingleton<IPlatformJsonSerializer, PlatformJsonSerializer>();
        services.AddSingleton<IPlatformErrorMapper, PlatformErrorMapper>();
        services.AddSingleton<IDiscoveryCache, DiscoveryCache>();
        services.AddSingleton<IPlatformSearchAdapter, NetEaseMusicSearchAdapter>();
        using var provider = services.BuildServiceProvider();

        var adapter = provider.GetRequiredService<IPlatformSearchAdapter>();

        Assert.IsType<NetEaseMusicSearchAdapter>(adapter);
        Assert.Equal(PlatformId.NetEaseMusic, adapter.Platform);
    }

    [Fact]
    public void CapabilitiesMatchTheAnonymousReferenceBoundary()
    {
        using var factory = Factory(Handler("netease-search-tracks-success.json"));
        var capabilities = Adapter(factory).Capabilities;

        Assert.True(capabilities.SupportsAnonymousTracks);
        Assert.True(capabilities.SupportsAnonymousAlbums);
        Assert.True(capabilities.SupportsAnonymousArtists);
        Assert.False(capabilities.SupportsAnonymousPlaylists);
        Assert.False(capabilities.SupportsAnonymousSuggestions);
    }

    [Fact]
    public async Task TrackSearchUsesReferenceWeapiEndpointWithoutCredentials()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        Uri? referer = null;
        string? body = null;
        bool hadCookie = false;
        bool hadAuthorization = false;
        var handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            method = request.Method;
            uri = request.RequestUri;
            referer = request.Headers.Referrer;
            hadCookie = request.Headers.Contains("Cookie");
            hadAuthorization = request.Headers.Authorization is not null;
            body = await request.Content!.ReadAsStringAsync(token);
            return FakeHttpMessageHandler.Json(Fixture("netease-search-tracks-success.json"));
        });
        using var factory = Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("music.163.com", uri!.Host);
        Assert.Equal("/weapi/cloudsearch/pc", uri.AbsolutePath);
        Assert.Equal("https://music.163.com/", referer!.AbsoluteUri);
        Assert.False(hadCookie);
        Assert.False(hadAuthorization);
        Assert.Contains("params=", body, StringComparison.Ordinal);
        Assert.Contains("encSecKey=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("周杰伦", body, StringComparison.Ordinal);
        Assert.Equal(SearchSourceState.Succeeded, response.State);

        var payload = NetEaseSearchProtocol.CreatePayload("周杰伦", 1, 30, 20);
        Assert.Equal("周杰伦", payload["s"]);
        Assert.Equal(1, payload["type"]);
        Assert.Equal(20, payload["limit"]);
        Assert.Equal(30, payload["offset"]);
        Assert.Equal(true, payload["total"]);
    }

    [Fact]
    public async Task TrackSearchMapsIdsMetadataDurationAndQuality()
    {
        using var factory = Factory(Handler("netease-search-tracks-success.json"));

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(42, response.TotalCount);
        Assert.True(response.HasMore);
        var track = response.Items[0];
        Assert.Equal(PlatformId.NetEaseMusic, track.Platform);
        Assert.Equal("186016", track.NativeId);
        Assert.Equal("netease:track:186016", track.StableId);
        Assert.Equal("晴天", track.Title);
        Assert.Equal("周杰伦", track.Artist);
        Assert.Equal("叶惠美", track.Album);
        Assert.Equal(TimeSpan.FromSeconds(269), track.Duration);
        Assert.Equal("320K", track.Quality);
        Assert.StartsWith("https://", track.CoverUri, StringComparison.Ordinal);
        Assert.Equal("网易云音乐", track.SourceDisplayName);
        Assert.False(track.IsPlayable);
        Assert.Equal("播放时需要登录并解析网易云音乐官方播放地址", track.RestrictionState);
        Assert.Equal("SQ", response.Items[1].Quality);
    }

    [Fact]
    public async Task AlbumSearchMapsNativeIdArtistCoverAndReleaseDate()
    {
        using var factory = Factory(Handler("netease-search-albums-success.json"));

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Albums), TestContext.Current.CancellationToken);

        var album = Assert.Single(response.Items);
        Assert.Equal(SearchResultType.Album, album.ResultType);
        Assert.Equal("18905", album.NativeId);
        Assert.Equal("netease:album:18905", album.StableId);
        Assert.Equal("叶惠美", album.Title);
        Assert.Contains("周杰伦", album.Subtitle);
        Assert.Contains("2003-07-31", album.Subtitle);
        Assert.Equal(7, response.TotalCount);
    }

    [Fact]
    public async Task ArtistSearchMapsNativeIdCoverAndDescription()
    {
        using var factory = Factory(Handler("netease-search-artists-success.json"));

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Artists), TestContext.Current.CancellationToken);

        var artist = Assert.Single(response.Items);
        Assert.Equal(SearchResultType.Artist, artist.ResultType);
        Assert.Equal("6452", artist.NativeId);
        Assert.Equal("netease:artist:6452", artist.StableId);
        Assert.Equal("华语歌手", artist.Subtitle);
        Assert.StartsWith("https://", artist.CoverUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllSearchRunsOnlyThreeSupportedFamiliesAndPreservesReportedTotals()
    {
        var handler = Handler("netease-search-all-success.json");
        using var factory = Factory(handler);
        var service = new MusicSearchService([Adapter(factory)]);

        var response = await service.SearchAsync(Query(SearchResultFilter.All), TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.SendCount);
        Assert.Equal(3, response.TotalCount);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Track);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Album);
        Assert.Contains(response.Items, item => item.ResultType == SearchResultType.Artist);
        Assert.DoesNotContain(response.Items, item => item.ResultType == SearchResultType.Playlist);
        Assert.False(response.IsPartialSuccess);
    }

    [Fact]
    public async Task AllSearchKeepsSuccessfulFamiliesWhenOneResponseNodeIsInvalid()
    {
        var handler = Handler("netease-search-all-partial.json");
        using var factory = Factory(handler);

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
        var handler = Handler("netease-search-tracks-success.json");
        using var factory = Factory(handler);

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Playlists), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Unsupported, response.State);
        Assert.Equal(PlatformErrorCode.Unsupported, response.ErrorCode);
        Assert.Empty(response.Items);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task SuggestionsAreUnsupportedAndDoNotUseHotSearchAsTypedSuggestions()
    {
        var handler = Handler("netease-search-tracks-success.json");
        using var factory = Factory(handler);

        var suggestions = await Adapter(factory).GetSuggestionsAsync("周杰伦", TestContext.Current.CancellationToken);

        Assert.Empty(suggestions);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task EmptyOrControlOnlyKeywordDoesNotEnterNetwork()
    {
        var handler = Handler("netease-search-tracks-success.json");
        using var factory = Factory(handler);

        var response = await Adapter(factory).SearchAsync(
            new SearchQuery(" \r\n\t ", SearchResultFilter.Tracks, SearchSourceScope.NetEase),
            TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Empty, response.State);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void PagingPayloadAndRequestKeysAreStableAndPlatformScoped()
    {
        var payload = NetEaseSearchProtocol.CreatePayload(" 周杰伦 ".Trim(), 10, 30, 30);
        var key = NetEaseSearchRequestKeys.Search(SearchResultFilter.Albums, "周杰伦", 2, 30);

        Assert.Equal(10, payload["type"]);
        Assert.Equal(30, payload["offset"]);
        Assert.Equal(30, payload["limit"]);
        Assert.Equal("netease:search:albums:周杰伦:2:30", key);
        RequestKeyValidator.Validate("netease", key);
        Assert.NotEqual(key, NetEaseSearchRequestKeys.Search(SearchResultFilter.Tracks, "周杰伦", 2, 30));
        Assert.NotEqual(key, NetEaseSearchRequestKeys.Search(SearchResultFilter.Albums, "周杰伦", 1, 30));
        Assert.NotEqual(key, QqSearchRequestKeys.Search(SearchResultFilter.Albums, "周杰伦", 2, 30));
    }

    [Fact]
    public void UnsafeKeywordUsesDeterministicNonSensitiveRequestKey()
    {
        var first = NetEaseSearchRequestKeys.Search(SearchResultFilter.Tracks, "token=fixture", 1, 30);
        var second = NetEaseSearchRequestKeys.Search(SearchResultFilter.Tracks, "token=fixture", 1, 30);

        Assert.Equal(first, second);
        Assert.DoesNotContain("token", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture", first, StringComparison.OrdinalIgnoreCase);
        RequestKeyValidator.Validate("netease", first);
    }

    [Fact]
    public async Task FreshCacheAvoidsSecondRequestAndMarksItems()
    {
        var handler = Handler("netease-search-tracks-success.json");
        using var factory = Factory(handler);
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
            _ => Task.FromResult(FakeHttpMessageHandler.Json(Fixture("netease-search-tracks-success.json"))),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var factory = Factory(handler);
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
            _ => Task.FromResult(FakeHttpMessageHandler.Json(Fixture("netease-search-tracks-success.json"))),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var factory = Factory(handler);
        var adapter = NetEaseSearchTestInfrastructure.Adapter(factory, new DiscoveryCache(), TimeSpan.FromMilliseconds(1));
        await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);
        var cachedBefore = DateTimeOffset.UtcNow;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        var fallback = await adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(SearchDataOrigin.CacheStale, fallback.DataOrigin);
        Assert.True(fallback.LoadedAt <= cachedBefore);
        Assert.All(fallback.Items, item => Assert.Equal(SearchDataOrigin.CacheStale, item.DataOrigin));
    }

    [Fact]
    public async Task SameInFlightSearchIsMergedBySharedPlatformClient()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(50), Fixture("netease-search-tracks-success.json"));
        using var factory = Factory(handler);
        var adapter = Adapter(factory);

        await Task.WhenAll(
            adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken),
            adapter.SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task CancelledSearchDoesNotPopulateCache()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(80), Fixture("netease-search-tracks-success.json"));
        using var factory = Factory(handler);
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
    [InlineData("netease-invalid-json.json", PlatformErrorCode.ParseFailure, SearchSourceState.Error)]
    [InlineData("netease-search-missing-required.json", PlatformErrorCode.InvalidResponse, SearchSourceState.Error)]
    [InlineData("netease-search-business-error.json", PlatformErrorCode.ServiceUnavailable, SearchSourceState.Error)]
    [InlineData("netease-search-unauthorized.json", PlatformErrorCode.Unauthorized, SearchSourceState.Unauthorized)]
    public async Task InvalidAndUnauthorizedPayloadsReturnSafeErrors(
        string fixture, PlatformErrorCode expectedCode, SearchSourceState expectedState)
    {
        using var factory = Factory(Handler(fixture));

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(expectedState, response.State);
        Assert.Equal(expectedCode, response.ErrorCode);
        Assert.Empty(response.Items);
        Assert.DoesNotContain("Exception", response.SafeMessage, StringComparison.OrdinalIgnoreCase);
        if (expectedCode == PlatformErrorCode.Unauthorized)
        {
            Assert.True(response.RequiresAuthorization);
            Assert.Contains("登录网易云音乐", response.SafeMessage);
        }
    }

    [Fact]
    public async Task EmptyResultIsSuccessfulAndNotPreview()
    {
        using var factory = Factory(Handler("netease-search-empty.json"));

        var response = await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Empty, response.State);
        Assert.Equal(SearchDataOrigin.Live, response.DataOrigin);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task SafeLogContainsOnlyHostAndPath()
    {
        var logger = new CaptureSafeLogger();
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-search-tracks-success.json"), logger);

        await Adapter(factory).SearchAsync(Query(SearchResultFilter.Tracks), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Started);
        Assert.Equal("music.163.com", entry.Host);
        Assert.Equal("/weapi/cloudsearch/pc", entry.Path);
        Assert.DoesNotContain("周杰伦", entry.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("params", entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SanitizedFixturesContainNoCredentialOrPlaybackMaterial()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetEase");
        foreach (var path in Directory.EnumerateFiles(directory, "netease-search-*.json"))
        {
            var text = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ToLowerInvariant();
            Assert.DoesNotContain("cookie", text);
            Assert.DoesNotContain("token", text);
            Assert.DoesNotContain("authorization", text);
            Assert.DoesNotContain("music_u", text);
            Assert.DoesNotContain("playurl", text);
        }
    }

    private static NetEaseMusicSearchAdapter Adapter(IPlatformHttpClientFactory factory) =>
        NetEaseSearchTestInfrastructure.Adapter(factory);

    private static PlatformHttpClientFactory Factory(HttpMessageHandler handler) =>
        NetEaseDiscoveryTestInfrastructure.Factory(handler);

    private static FakeHttpMessageHandler Handler(string fixture) =>
        NetEaseSearchTestInfrastructure.Handler(fixture);

    private static SearchQuery Query(SearchResultFilter filter, bool forceRefresh = false) =>
        new("周杰伦", filter, SearchSourceScope.NetEase, ForceRefresh: forceRefresh);

    private static string Fixture(string name) => NetEaseSearchTestInfrastructure.Fixture(name);
}
