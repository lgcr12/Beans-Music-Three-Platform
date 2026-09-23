using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Services.Security;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqPlatformLibraryAdapterTests
{
    [Fact]
    public async Task SearchResultsPreserveProviderMediaMidForPlayback()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(JsonFixture("qq-search-tracks-success.json")));
        using var factory = Factory(handler);
        var adapter = new QqMusicSearchAdapter(factory, new PlatformJsonSerializer(),
            new PlatformErrorMapper(), new DiscoveryCache());

        var response = await adapter.SearchAsync(
            new SearchQuery("周杰伦", SearchResultFilter.Tracks, SearchSourceScope.Qq),
            TestContext.Current.CancellationToken);

        Assert.Equal("MEDIAMID001", response.Items[0].ProviderMediaId);
        Assert.Equal("TRACKMID002", response.Items[1].ProviderMediaId);
    }

    [Fact]
    public async Task MissingSessionReturnsNotAuthorizedWithoutNetwork()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        using var factory = Factory(handler);
        var adapter = new QqPlatformLibraryAdapter(factory, new MemoryCredentialStore(), new PlatformJsonSerializer());

        var snapshot = await adapter.LoadAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.NotAuthorized, snapshot.State);
        Assert.Equal(CredentialState.NotAuthorized, snapshot.Probe.CredentialState);
        Assert.Empty(snapshot.Playlists);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task AuthorizedAccountLoadsProfileFavoriteAndCreatedPlaylists()
    {
        var handler = AccountHandler();
        using var factory = Factory(handler);
        var store = await AuthorizedStoreAsync();
        var adapter = new QqPlatformLibraryAdapter(factory, store, new PlatformJsonSerializer());

        var snapshot = await adapter.LoadAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.Succeeded, snapshot.State);
        Assert.Equal(CredentialState.Valid, snapshot.Probe.CredentialState);
        Assert.Equal("测试用户", snapshot.Probe.DisplayName);
        Assert.Equal("12345", snapshot.Probe.NativeUserId);
        Assert.Equal(PlatformMembershipState.Unknown, snapshot.Probe.MembershipState);
        Assert.Equal(3, snapshot.Playlists.Count);
        var favorite = Assert.Single(snapshot.Playlists, item => item.IsFavoriteCollection);
        Assert.Equal("favorite", favorite.NativeKind);
        Assert.Equal("201", favorite.NativeId);
        Assert.Contains(snapshot.Playlists, item => item.NativeKind == "diss" && item.NativeId == "7788");
        Assert.Contains(snapshot.Playlists, item => item.NativeKind == "tid" && item.NativeId == "9900");
        Assert.Equal(3, handler.SendCount);
    }

    [Fact]
    public async Task SupplementaryCreatedEndpointFailureKeepsPrimaryPlaylistsAsPartialSuccess()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("profile_homepage", StringComparison.Ordinal))
                return Task.FromResult(JsonFixture("qq-account-profile-success.json"));
            if (path.Contains("profile_order_asset", StringComparison.Ordinal))
                return Task.FromResult(JsonFixture("qq-account-assets-success.json"));
            return Task.FromResult(FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        });
        using var factory = Factory(handler);
        var adapter = new QqPlatformLibraryAdapter(factory, await AuthorizedStoreAsync(), new PlatformJsonSerializer());

        var snapshot = await adapter.LoadAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.Partial, snapshot.State);
        Assert.True(snapshot.IsPartialSuccess);
        Assert.Equal(2, snapshot.Playlists.Count);
        Assert.Equal(CredentialState.Valid, snapshot.Probe.CredentialState);
    }

    [Fact]
    public async Task FavoriteTracksPreserveSongMidAndProviderMediaMid()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            Assert.Contains("fcg_musiclist_getmyfav", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("Cookie", request.Headers.Select(header => header.Key));
            return Task.FromResult(JsonFixture("qq-account-favorite-tracks-success.json"));
        });
        using var factory = Factory(handler);
        var adapter = new QqPlatformLibraryAdapter(factory, await AuthorizedStoreAsync(), new PlatformJsonSerializer());
        var playlist = new PlatformUserPlaylist(
            PlatformId.QqMusic, "201", "favorite", "我喜欢", "测试用户", string.Empty, 1, true);

        var tracks = await adapter.LoadPlaylistTracksAsync(playlist, TestContext.Current.CancellationToken);

        var track = Assert.Single(tracks);
        Assert.Equal("SONGMID001", track.NativeId);
        Assert.Equal("MEDIAMID001", track.ProviderMediaId);
        Assert.Equal("晴天", track.Title);
        Assert.Equal("周杰伦", track.Artist);
        Assert.Equal("叶惠美", track.Album);
        Assert.Equal("320K", track.Quality);
        Assert.Equal(TimeSpan.FromSeconds(269), track.Duration);
        Assert.Equal(AvailabilityState.Available, track.Availability);
    }

    private static FakeHttpMessageHandler AccountHandler() => new((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var fixture = path.Contains("profile_homepage", StringComparison.Ordinal)
            ? "qq-account-profile-success.json"
            : path.Contains("profile_order_asset", StringComparison.Ordinal)
                ? "qq-account-assets-success.json"
                : "qq-account-created-success.json";
        return Task.FromResult(JsonFixture(fixture));
    });

    private static HttpResponseMessage JsonFixture(string name) =>
        FakeHttpMessageHandler.Json(QqDiscoveryTestInfrastructure.Fixture(name));

    private static async Task<MemoryCredentialStore> AuthorizedStoreAsync()
    {
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=o12345; p_skey=gtk-key; qm_keyst=playback-key",
            TestContext.Current.CancellationToken);
        return store;
    }

    private static PlatformHttpClientFactory Factory(HttpMessageHandler handler) =>
        new(
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["qq"] = handler,
                ["netease"] = handler,
                ["kugou"] = handler
            },
            new CaptureSafeLogger(), new SensitiveDataRedactor(), new PlatformErrorMapper());

    private sealed class MemoryCredentialStore : ISecureCredentialStore
    {
        private readonly Dictionary<(string Platform, string Name), string> _values = new();

        public Task SaveAsync(string platformId, string secretName, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[(platformId, secretName)] = value;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string platformId, string secretName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue((platformId, secretName), out var value) ? value : null);
        }

        public Task DeletePlatformAsync(string platformId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in _values.Keys.Where(key => key.Platform == platformId).ToArray()) _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
