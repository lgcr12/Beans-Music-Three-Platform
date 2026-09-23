using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEaseDiscoveryAdapterTests
{
    [Fact]
    public void AdapterDeclaresOnlyPhase5CImplementedCapabilities()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-rankings-success.json"));
        var capabilities = Adapter(factory).Capabilities;

        Assert.True(capabilities.SupportsAnonymousHero);
        Assert.True(capabilities.SupportsAnonymousRankings);
        Assert.True(capabilities.SupportsAnonymousRecommendedPlaylists);
        Assert.True(capabilities.SupportsAnonymousPlaylistSquare);
        Assert.True(capabilities.SupportsAnonymousCategories);
        Assert.True(capabilities.SupportsAuthenticatedDailyRecommendations);
        Assert.False(capabilities.SupportsAuthenticatedUserPlaylists);
        Assert.False(capabilities.SupportsSearch);
        Assert.False(capabilities.SupportsPlayback);
        Assert.Equal("netease", Adapter(factory).PlatformId);
    }

    [Fact]
    public async Task RankingsUseReferenceWeapiEndpointWithoutCredentials()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync(token);
            return FakeHttpMessageHandler.Json(Fixture("netease-rankings-success.json"));
        });
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(handler);

        var response = await Adapter(factory).GetPublicRankingsAsync(
            Context("netease.discovery.rankings", NetEaseDiscoveryRequestKeys.Rankings),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("music.163.com", captured.RequestUri!.Host);
        Assert.Equal("/weapi/toplist/detail", captured.RequestUri.AbsolutePath);
        Assert.Equal("https://music.163.com/", captured.Headers.Referrer!.AbsoluteUri);
        Assert.False(captured.Headers.Contains("Cookie"));
        Assert.Null(captured.Headers.Authorization);
        Assert.Contains("params=", body, StringComparison.Ordinal);
        Assert.Contains("encSecKey=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("csrf_token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RankingsMapNativeIdsOptionalCoverAndTrackSummaries()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-rankings-success.json"));
        var response = await Adapter(factory).GetPublicRankingsAsync(
            Context("netease.discovery.rankings", NetEaseDiscoveryRequestKeys.Rankings),
            TestContext.Current.CancellationToken);

        var rankings = response.Value!;
        Assert.Equal(["19723756", "3779629"], rankings.Select(item => item.Id));
        Assert.All(rankings, item => Assert.Equal("netease", item.PlatformId));
        Assert.Equal(["Fixture Track One", "Fixture Track Two", "Fixture Track Three"], rankings[0].TopTracks);
        Assert.Equal("ms-appx:///Assets/Branding/beans-icon.png", rankings[1].ImageUri);
        Assert.DoesNotContain(rankings, item => item.Id == item.Title);
    }

    [Fact]
    public async Task RecommendedPlaylistsMapRealMetadataWithoutInventingOptionalValues()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-recommended-playlists-success.json"));
        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("netease.discovery.recommended-playlists", NetEaseDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        var first = response.Value![0];
        Assert.Equal(PlatformId.NetEaseMusic, first.Identity.Platform);
        Assert.Equal("910001", first.Identity.NativeId);
        Assert.Equal(30, first.TrackCount);
        Assert.Equal(456789, first.PlayCount);
        Assert.Equal("华语", first.Category);
        Assert.Equal("NetEase Editorial", first.Creator);
        Assert.Equal("netease:playlist:910001", first.PayloadReference);
        Assert.Equal("网易云音乐", first.SourceBadgeText);
        Assert.False(first.IsPlayable);
        Assert.Null(response.Value[1].TrackCount);
        Assert.Null(response.Value[1].PlayCount);
    }

    [Fact]
    public async Task HeroComesFromPublicPlaylistAndPreservesNativeId()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-hero-success.json"));
        var response = await Adapter(factory).GetHeroAsync(
            Context("netease.discovery.hero", NetEaseDiscoveryRequestKeys.Hero),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal("Fixture NetEase Editorial", response.Value!.Title);
        Assert.Equal("910001", response.Value.TrackId);
        Assert.Contains("公开推荐歌单", response.Value.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CategoriesAndPlaylistSquareUseReferenceEndpoints()
    {
        var paths = new List<string>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            var json = request.RequestUri.AbsolutePath.EndsWith("catlist", StringComparison.Ordinal)
                ? Fixture("netease-categories-success.json")
                : Fixture("netease-playlist-square-success.json");
            return Task.FromResult(FakeHttpMessageHandler.Json(json));
        });
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);

        var categories = await adapter.GetCategoriesAsync(
            Context("netease.discovery.categories", NetEaseDiscoveryRequestKeys.Categories),
            TestContext.Current.CancellationToken);
        var square = await adapter.GetPublicPlaylistSquareAsync("摇滚", 1,
            Context("netease.discovery.playlist-square", NetEaseDiscoveryRequestKeys.PlaylistSquare("摇滚", 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(["华语", "流行", "摇滚"], categories.Value);
        Assert.Single(square.Value!);
        Assert.Equal("920001", square.Value![0].Identity.NativeId);
        Assert.Contains("/weapi/playlist/catlist", paths);
        Assert.Contains("/weapi/playlist/list", paths);
    }

    [Fact]
    public async Task DailyRecommendationsRequireAuthorizationWithoutNetwork()
    {
        var handler = Handler("netease-daily-unauthorized.json");
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(handler);
        var response = await Adapter(factory).GetDailyRecommendationsAsync(
            Context("netease.discovery.daily-recommendations", "netease:discovery:daily-recommendations:anonymous"),
            TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccess);
        Assert.Equal(PlatformErrorCode.Unauthorized, response.Error!.Code);
        Assert.True(response.Error.RequiresLogin);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task EmptyPlaylistListIsSafeAndEmptyHeroIsInvalidResponse()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-empty.json"));
        var adapter = Adapter(factory);
        var playlists = await adapter.GetPublicRecommendedPlaylistsAsync(
            Context("netease.discovery.recommended-playlists", NetEaseDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);
        var hero = await adapter.GetHeroAsync(
            Context("netease.discovery.hero", NetEaseDiscoveryRequestKeys.Hero),
            TestContext.Current.CancellationToken);

        Assert.True(playlists.IsSuccess);
        Assert.Empty(playlists.Value!);
        Assert.Equal(PlatformErrorCode.InvalidResponse, hero.Error!.Code);
    }

    [Theory]
    [InlineData("netease-invalid-json.json", PlatformErrorCode.ParseFailure)]
    [InlineData("netease-missing-required-field.json", PlatformErrorCode.InvalidResponse)]
    public async Task InvalidResponsesMapToSafeErrors(string fixture, PlatformErrorCode expected)
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler(fixture));
        var response = await Adapter(factory).GetPublicRankingsAsync(
            Context("netease.discovery.rankings", NetEaseDiscoveryRequestKeys.Rankings),
            TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccess);
        Assert.Equal(expected, response.Error!.Code);
        Assert.DoesNotContain("Exception", response.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpSuccessWithBusinessErrorMapsToServiceUnavailable()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-business-error.json"));
        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("netease.discovery.recommended-playlists", NetEaseDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.Equal(PlatformErrorCode.ServiceUnavailable, response.Error!.Code);
    }

    [Fact]
    public async Task AnonymousUnauthorizedBusinessResponseMapsToLoginBoundary()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-daily-unauthorized.json"));
        var response = await Adapter(factory).GetPublicRecommendedPlaylistsAsync(
            Context("netease.discovery.recommended-playlists", NetEaseDiscoveryRequestKeys.RecommendedPlaylists),
            TestContext.Current.CancellationToken);

        Assert.Equal(PlatformErrorCode.Unauthorized, response.Error!.Code);
        Assert.True(response.Error.RequiresLogin);
    }

    [Fact]
    public async Task SameNetEaseRequestIsMergedBySharedNetworkLayer()
    {
        var handler = new DelayedHttpMessageHandler(TimeSpan.FromMilliseconds(50), Fixture("netease-rankings-success.json"));
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(handler);
        var adapter = Adapter(factory);

        await Task.WhenAll(
            adapter.GetPublicRankingsAsync(Context("netease.discovery.rankings", NetEaseDiscoveryRequestKeys.Rankings), TestContext.Current.CancellationToken),
            adapter.GetPublicRankingsAsync(Context("netease.discovery.rankings", NetEaseDiscoveryRequestKeys.Rankings), TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task LogsContainOnlyHostAndPath()
    {
        var logger = new CaptureSafeLogger();
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(Handler("netease-rankings-success.json"), logger);
        await Adapter(factory).GetPublicRankingsAsync(
            Context("netease.discovery.rankings", NetEaseDiscoveryRequestKeys.Rankings),
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Started);
        Assert.Equal("music.163.com", entry.Host);
        Assert.Equal("/weapi/toplist/detail", entry.Path);
        Assert.DoesNotContain("params", entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestKeysAreScopedAndContainNoSensitiveMaterial()
    {
        var keys = new[]
        {
            NetEaseDiscoveryRequestKeys.Hero,
            NetEaseDiscoveryRequestKeys.Rankings,
            NetEaseDiscoveryRequestKeys.RecommendedPlaylists,
            NetEaseDiscoveryRequestKeys.Categories,
            NetEaseDiscoveryRequestKeys.PlaylistSquare("摇滚", 2)
        };
        Assert.All(keys, key =>
        {
            Assert.StartsWith("netease:", key, StringComparison.Ordinal);
            RequestKeyValidator.Validate("netease", key);
        });
    }

    [Fact]
    public async Task FixturesContainNoCredentialPlaybackOrPersonalFields()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetEase");
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var text = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ToLowerInvariant();
            Assert.DoesNotContain("cookie", text);
            Assert.DoesNotContain("token", text);
            Assert.DoesNotContain("authorization", text);
            Assert.DoesNotContain("music_u", text);
            Assert.DoesNotContain("playurl", text);
        }
    }

    private static NetEaseMusicDiscoveryAdapter Adapter(IPlatformHttpClientFactory factory) =>
        new(factory, new PlatformJsonSerializer(), new PlatformErrorMapper());

    private static PlatformRequestContext Context(string operation, string key) =>
        NetEaseDiscoveryTestInfrastructure.Context(operation, key, TestContext.Current.CancellationToken);

    private static string Fixture(string name) => NetEaseDiscoveryTestInfrastructure.Fixture(name);

    private static FakeHttpMessageHandler Handler(string fixture) => new((_, _) =>
        Task.FromResult(FakeHttpMessageHandler.Json(Fixture(fixture))));
}
