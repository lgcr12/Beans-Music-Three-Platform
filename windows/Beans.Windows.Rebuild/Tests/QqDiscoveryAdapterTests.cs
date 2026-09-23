using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqDiscoveryAdapterTests
{
    [Fact]
    public void AdapterDeclaresOnlyImplementedAnonymousCapabilities()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-rankings-success.json"));
        var adapter = Adapter(factory);

        Assert.Equal("qq", adapter.PlatformId);
        Assert.True(adapter.Capabilities.SupportsAnonymousHero);
        Assert.True(adapter.Capabilities.SupportsAnonymousRankings);
        Assert.True(adapter.Capabilities.SupportsAnonymousRecommendedPlaylists);
        Assert.False(adapter.Capabilities.SupportsAnonymousPlaylistSquare);
        Assert.False(adapter.Capabilities.SupportsAnonymousCategories);
        Assert.True(adapter.Capabilities.SupportsAuthenticatedDailyRecommendations);
        Assert.False(adapter.Capabilities.SupportsSearch);
        Assert.False(adapter.Capabilities.SupportsPlayback);
    }

    [Fact]
    public async Task RankingsUseReferenceGetEndpointAndMapStableIds()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        Uri? referer = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            method = request.Method;
            uri = request.RequestUri;
            referer = request.Headers.Referrer;
            return Task.FromResult(FakeHttpMessageHandler.Json(QqDiscoveryTestInfrastructure.Fixture("qq-rankings-success.json")));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);
        var context = Context("qq.discovery.rankings", QqDiscoveryRequestKeys.Rankings);

        var response = await adapter.GetPublicRankingsAsync(context, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("c.y.qq.com", uri!.Host);
        Assert.Equal("/v8/fcg-bin/fcg_myqq_toplist.fcg", uri.AbsolutePath);
        Assert.Equal("https://y.qq.com/", referer!.AbsoluteUri);
        var rankings = response.Value!;
        Assert.Equal(["26", "27"], rankings.Select(item => item.Id));
        Assert.All(rankings, item => Assert.Equal("qq", item.PlatformId));
        Assert.Equal(["Fixture Song One", "Fixture Song Two", "Fixture Song Three"], rankings[0].TopTracks);
        Assert.DoesNotContain(rankings, item => item.Id == item.Title);
    }

    [Fact]
    public async Task RecommendedPlaylistsUseAnonymousReferencePostWithoutCredentials()
    {
        HttpMethod? method = null;
        Uri? uri = null;
        string? body = null;
        bool hadCookie = false;
        bool hadAuthorization = false;
        var handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            method = request.Method;
            uri = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync(token);
            hadCookie = request.Headers.Contains("Cookie");
            hadAuthorization = request.Headers.Authorization is not null;
            return FakeHttpMessageHandler.Json(QqDiscoveryTestInfrastructure.Fixture("qq-recommended-playlists-success.json"));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);

        var response = await adapter.GetPublicRecommendedPlaylistsAsync(
            Context("qq.discovery.recommended-playlists", QqDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("u.y.qq.com", uri!.Host);
        Assert.Equal("/cgi-bin/musicu.fcg", uri.AbsolutePath);
        Assert.Contains("music.srfDissInfo.RecommendPlaylist", body, StringComparison.Ordinal);
        Assert.Contains("\"uin\":0", body, StringComparison.Ordinal);
        Assert.False(hadCookie);
        Assert.False(hadAuthorization);
        Assert.Equal(2, response.Value!.Count);
    }

    [Fact]
    public async Task RetiredRecommendationMethodFallsBackToPublicPlaylistSquare()
    {
        Uri? fallbackUri = null;
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
                return Task.FromResult(FakeHttpMessageHandler.Json(QqDiscoveryTestInfrastructure.Fixture("qq-business-error.json")));
            fallbackUri = request.RequestUri;
            return Task.FromResult(FakeHttpMessageHandler.Json("{\"code\":0,\"data\":{\"list\":[{\"dissid\":\"990001\",\"dissname\":\"Live public playlist\",\"imgurl\":\"//y.gtimg.cn/cover.jpg\",\"songnum\":12,\"listennum\":456,\"creator\":{\"name\":\"QQ Music\"}}]}}"));
        });
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("qq.discovery.recommended-playlists", QqDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("990001", response.Value![0].Identity.NativeId);
        Assert.Contains("fcg_get_diss_by_tag.fcg", fallbackUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task PlaylistMapperPreservesNativeMetadataAndOptionalFields()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-recommended-playlists-success.json"));
        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("qq.discovery.recommended-playlists", QqDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        var first = response.Value![0];
        Assert.Equal(PlatformId.QqMusic, first.Identity.Platform);
        Assert.Equal("880001", first.Identity.NativeId);
        Assert.Equal(24, first.TrackCount);
        Assert.Equal(123456, first.PlayCount);
        Assert.Equal("QQ Music Editorial", first.Creator);
        Assert.Equal("QQ 音乐", first.SourceBadgeText);
        Assert.Equal("qq:playlist:880001", first.PayloadReference);
        Assert.False(first.IsPlayable);

        var optional = response.Value[1];
        Assert.Null(optional.TrackCount);
        Assert.Equal(string.Empty, optional.TrackCountText);
        Assert.Equal("QQ 音乐", optional.Creator);
    }

    [Fact]
    public async Task MissingCoverUsesLocalPlaceholderWithoutFailingSection()
    {
        const string json = "{\"code\":0,\"req_1\":{\"code\":0,\"data\":{\"v_playlist\":[{\"tid\":9,\"title\":\"No cover\"}]}}}";
        using var factory = QqDiscoveryTestInfrastructure.Factory(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json(json))));

        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("qq.discovery.recommended-playlists", QqDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("ms-appx:///Assets/Branding/beans-icon.png", response.Value![0].CoverUri);
    }

    [Fact]
    public async Task HeroComesFromRealRecommendedPlaylistMapping()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-recommended-playlists-success.json"));
        var response = await Adapter(factory).GetHeroAsync(
            Context("qq.discovery.hero", QqDiscoveryRequestKeys.Hero), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("Fixture Public Playlist", response.Value!.Title);
        Assert.Equal("880001", response.Value.TrackId);
        Assert.DoesNotContain("Preview", response.Value.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyListIsASafeSuccessfulResponse()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-empty.json"));
        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("qq.discovery.recommended-playlists", QqDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Empty(response.Value!);
    }

    [Fact]
    public async Task EmptyRecommendedListReturnsSafeHeroError()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-empty.json"));
        var response = await Adapter(factory).GetHeroAsync(
            Context("qq.discovery.hero", QqDiscoveryRequestKeys.Hero), TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccess);
        Assert.Equal(PlatformErrorCode.InvalidResponse, response.Error!.Code);
        Assert.DoesNotContain("Exception", response.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingRequiredFieldMapsToInvalidResponse()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-missing-required-field.json"));
        var response = await Adapter(factory).GetPublicRankingsAsync(
            Context("qq.discovery.rankings", QqDiscoveryRequestKeys.Rankings), TestContext.Current.CancellationToken);

        Assert.Equal(PlatformErrorCode.InvalidResponse, response.Error!.Code);
    }

    [Fact]
    public async Task InvalidJsonMapsToParseFailure()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-invalid-json.json"));
        var response = await Adapter(factory).GetPublicRankingsAsync(
            Context("qq.discovery.rankings", QqDiscoveryRequestKeys.Rankings), TestContext.Current.CancellationToken);

        Assert.Equal(PlatformErrorCode.ParseFailure, response.Error!.Code);
    }

    [Fact]
    public async Task HttpSuccessWithQqBusinessErrorMapsToServiceUnavailable()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-business-error.json"));
        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("qq.discovery.recommended-playlists", QqDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.Equal(PlatformErrorCode.ServiceUnavailable, response.Error!.Code);
    }

    [Theory]
    [InlineData("categories")]
    [InlineData("square")]
    public async Task UnsupportedCapabilitiesDoNotEnterNetwork(string capability)
    {
        var handler = Handler("qq-rankings-success.json");
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);
        PlatformResponse<object> boxed;

        if (capability == "categories")
        {
            var value = await adapter.GetCategoriesAsync(
                Context("qq.discovery.categories", QqDiscoveryRequestKeys.Categories), TestContext.Current.CancellationToken);
            boxed = Box(value);
        }
        else
        {
            var value = await adapter.GetPublicPlaylistSquareAsync(null, 1,
                Context("qq.discovery.playlist-square", QqDiscoveryRequestKeys.PlaylistSquare(null, 1)),
                TestContext.Current.CancellationToken);
            boxed = Box(value);
        }

        Assert.Equal(PlatformErrorCode.Unsupported, boxed.Error!.Code);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task SameQqRequestIsMergedByPhase5ANetworkLayer()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(50),
            QqDiscoveryTestInfrastructure.Fixture("qq-rankings-success.json"));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);
        var context1 = Context("qq.discovery.rankings", QqDiscoveryRequestKeys.Rankings);
        var context2 = Context("qq.discovery.rankings", QqDiscoveryRequestKeys.Rankings);

        await Task.WhenAll(
            adapter.GetPublicRankingsAsync(context1, TestContext.Current.CancellationToken),
            adapter.GetPublicRankingsAsync(context2, TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task QqLogReceivesPathButNeverQueryOrRequestBody()
    {
        var logger = new CaptureSafeLogger();
        using var factory = QqDiscoveryTestInfrastructure.Factory(Handler("qq-rankings-success.json"), logger);
        await Adapter(factory).GetPublicRankingsAsync(
            Context("qq.discovery.rankings", QqDiscoveryRequestKeys.Rankings), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Started);
        Assert.Equal("c.y.qq.com", entry.Host);
        Assert.Equal("/v8/fcg-bin/fcg_myqq_toplist.fcg", entry.Path);
        Assert.DoesNotContain("format", entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QqRequestKeysArePlatformScopedAndContainNoSensitiveMaterial()
    {
        var keys = new[]
        {
            QqDiscoveryRequestKeys.Hero,
            QqDiscoveryRequestKeys.Rankings,
            QqDiscoveryRequestKeys.RecommendedPlaylists,
            QqDiscoveryRequestKeys.Categories,
            QqDiscoveryRequestKeys.PlaylistSquare("public", 1)
        };
        Assert.All(keys, key =>
        {
            Assert.StartsWith("qq:", key, StringComparison.Ordinal);
            RequestKeyValidator.Validate("qq", key);
        });
    }

    [Fact]
    public async Task DailyRecommendationsMixReferenceChartsAndDeduplicateTracks()
    {
        var body = "{\"code\":0,\"songlist\":[{\"data\":{\"mid\":\"daily-mid\",\"name\":\"Daily track\",\"singer\":[{\"mid\":\"singer\",\"name\":\"Singer\"}],\"album\":{\"mid\":\"album\",\"name\":\"Album\"},\"interval\":210,\"file\":{\"size_128mp3\":100}}}]}";
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        }));
        using var factory = QqDiscoveryTestInfrastructure.Factory(handler);
        var response = await Adapter(factory).GetDailyRecommendationsAsync(
            Context("qq.discovery.daily-recommendations", "qq:discovery:daily-recommendations:anonymous"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Single(response.Value!);
        Assert.Equal("daily-mid", response.Value![0].Identity.NativeId);
        Assert.Equal(3, handler.SendCount);
    }

    [Fact]
    public async Task QqFixturesContainNoCredentialOrPersonalFields()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "QQ");
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var text = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ToLowerInvariant();
            Assert.DoesNotContain("cookie", text);
            Assert.DoesNotContain("token", text);
            Assert.DoesNotContain("authorization", text);
            Assert.DoesNotContain("qqmusic_key", text);
            Assert.DoesNotContain("play_url", text);
        }
    }

    private static QqMusicDiscoveryAdapter Adapter(IPlatformHttpClientFactory factory) =>
        new(factory, new PlatformJsonSerializer(), new PlatformErrorMapper());

    private static PlatformRequestContext Context(string operation, string key) =>
        QqDiscoveryTestInfrastructure.Context(operation, key, TestContext.Current.CancellationToken);

    private static FakeHttpMessageHandler Handler(string fixture) => new((_, _) =>
        Task.FromResult(FakeHttpMessageHandler.Json(QqDiscoveryTestInfrastructure.Fixture(fixture))));

    private static PlatformResponse<object> Box<T>(PlatformResponse<T> response) => new(
        response.IsSuccess, response.Value, response.Error, response.StatusCode, response.CorrelationId,
        response.Elapsed, response.RetryCount, response.DataOrigin, response.LoadedAt, response.IsStale,
        response.CacheKey, response.SafeMessage);
}
