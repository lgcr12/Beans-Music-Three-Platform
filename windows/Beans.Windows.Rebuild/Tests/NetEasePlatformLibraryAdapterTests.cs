using System.Net;
using System.Collections.Concurrent;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.PlatformLibrary;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Security;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEasePlatformLibraryAdapterTests
{
    [Fact]
    public async Task MissingSessionReturnsNotAuthorizedWithoutNetwork()
    {
        var handler = Handler((_, _) => throw new InvalidOperationException("Network must not be used."));
        using var factory = Factory(handler);
        var adapter = new NetEasePlatformLibraryAdapter(factory, new MemoryCredentialStore(), new PlatformJsonSerializer());

        var result = await adapter.LoadAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.NotAuthorized, result.State);
        Assert.Equal(CredentialState.NotAuthorized, result.Probe.CredentialState);
        Assert.Empty(result.Playlists);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task AccountMembershipAndPlaylistsAreMappedWithNativeKinds()
    {
        var cookies = new ConcurrentBag<string>();
        var handler = Handler(async (request, cancellationToken) =>
        {
            cookies.Add(request.Headers.GetValues("Cookie").Single());
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/nuser/account/get", StringComparison.Ordinal))
                return Json("""{"code":200,"profile":{"userId":42,"nickname":"测试用户"}}""");
            if (path.EndsWith("/vip/info", StringComparison.Ordinal))
                return Json("""{"code":200,"data":{"redVipLevel":6}}""");
            Assert.Equal("42", QueryValue(request.RequestUri, "uid"));
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Json("""
                {"code":200,"playlist":[
                  {"id":100,"name":"我喜欢的音乐","coverImgUrl":"http://img.example/favorite.jpg","trackCount":8,"specialType":5,"subscribed":false,"creator":{"userId":42,"nickname":"测试用户"}},
                  {"id":101,"name":"自建歌单","coverImgUrl":"https://img.example/created.jpg","trackCount":3,"specialType":0,"subscribed":false,"creator":{"userId":42,"nickname":"测试用户"}},
                  {"id":102,"name":"收藏歌单","coverImgUrl":"https://img.example/subscribed.jpg","trackCount":5,"specialType":0,"subscribed":true,"creator":{"userId":7,"nickname":"其他用户"}}
                ]}
                """);
        });
        using var factory = Factory(handler);
        var store = await AuthorizedStoreAsync();
        var adapter = new NetEasePlatformLibraryAdapter(factory, store, new PlatformJsonSerializer());

        var result = await adapter.LoadAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.Succeeded, result.State);
        Assert.Equal("测试用户", result.Probe.DisplayName);
        Assert.Equal("42", result.Probe.NativeUserId);
        Assert.Equal(PlatformMembershipState.Active, result.Probe.MembershipState);
        Assert.Equal("黑胶 VIP", result.Probe.MembershipLabel);
        Assert.Collection(result.Playlists,
            item =>
            {
                Assert.Equal((PlatformId.NetEaseMusic, "100", "favorites"), (item.Platform, item.NativeId, item.NativeKind));
                Assert.True(item.IsFavoriteCollection);
                Assert.StartsWith("https://", item.CoverUri, StringComparison.Ordinal);
            },
            item => Assert.Equal("created", item.NativeKind),
            item => Assert.Equal("subscribed", item.NativeKind));
        Assert.All(cookies, value => Assert.Equal("MUSIC_U=opaque; __csrf=csrf-value", value));
    }

    [Fact]
    public async Task MembershipFailureKeepsPlaylistsAsExplicitPartialSuccess()
    {
        var handler = Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                var value when value.EndsWith("/nuser/account/get", StringComparison.Ordinal) =>
                    Json("""{"code":200,"profile":{"userId":42,"nickname":"测试用户"}}"""),
                var value when value.EndsWith("/vip/info", StringComparison.Ordinal) =>
                    Json("""{"code":500,"data":null}"""),
                _ => Json("""{"code":200,"playlist":[{"id":101,"name":"自建歌单","trackCount":1,"creator":{"userId":42,"nickname":"测试用户"}}]}""")
            });
        });
        using var factory = Factory(handler);
        var adapter = new NetEasePlatformLibraryAdapter(factory, await AuthorizedStoreAsync(), new PlatformJsonSerializer());

        var result = await adapter.LoadAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.Partial, result.State);
        Assert.True(result.IsPartialSuccess);
        Assert.Single(result.Playlists);
        Assert.Equal(PlatformMembershipState.Unknown, result.Probe.MembershipState);
        Assert.DoesNotContain("500", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedAccountReturnsExpiredWithoutLoadingPlaylists()
    {
        var handler = Handler((request, _) =>
        {
            Assert.EndsWith("/nuser/account/get", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(Json("""{"code":301,"profile":null}"""));
        });
        using var factory = Factory(handler);
        var adapter = new NetEasePlatformLibraryAdapter(factory, await AuthorizedStoreAsync(), new PlatformJsonSerializer());

        var result = await adapter.LoadAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformLibraryState.NotAuthorized, result.State);
        Assert.Equal(CredentialState.Expired, result.Probe.CredentialState);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PlaylistTracksLoadMissingDetailsInBatchesAndPreserveTrackOrder()
    {
        string? detailBody = null;
        var handler = Handler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/playlist/detail", StringComparison.Ordinal))
                return Json("""
                    {"code":200,"playlist":{"tracks":[
                      {"id":1,"name":"第一首","ar":[{"name":"歌手甲"}],"al":{"name":"专辑甲","picUrl":"https://img.example/1.jpg"},"dt":180000,"h":{"br":320000},"fee":0}
                    ],"trackIds":[{"id":1},{"id":2},{"id":3}]}}
                    """);

            Assert.EndsWith("/song/detail", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            detailBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("""
                {"code":200,"songs":[
                  {"id":3,"name":"第三首","artists":[{"name":"歌手丙"}],"album":{"name":"专辑丙","picUrl":"http://img.example/3.jpg"},"duration":210000,"sq":{"br":999000},"fee":1},
                  {"id":2,"name":"第二首","ar":[{"name":"歌手乙"}],"al":{"name":"专辑乙","picUrl":"https://img.example/2.jpg"},"dt":200000,"m":{"br":192000},"privilege":{"st":-200}}
                ]}
                """);
        });
        using var factory = Factory(handler);
        var adapter = new NetEasePlatformLibraryAdapter(factory, await AuthorizedStoreAsync(), new PlatformJsonSerializer());
        var playlist = new PlatformUserPlaylist(PlatformId.NetEaseMusic, "900", "created", "歌单", "用户", "", 3);

        var tracks = await adapter.LoadPlaylistTracksAsync(playlist, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "1", "2", "3" }, tracks.Select(item => item.NativeId).ToArray());
        Assert.Equal(AvailabilityState.Available, tracks[0].Availability);
        Assert.Equal(AvailabilityState.Unavailable, tracks[1].Availability);
        Assert.Equal(AvailabilityState.SubscriptionRequired, tracks[2].Availability);
        Assert.Equal("SQ", tracks[2].Quality);
        Assert.Contains("%7B%22id%22%3A2%7D", detailBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%7B%22id%22%3A3%7D", detailBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opaque", detailBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaylistTrackAuthorizationFailureUsesSafeException()
    {
        var handler = Handler((_, _) => Task.FromResult(Json("""{"code":301,"playlist":null}""")));
        using var factory = Factory(handler);
        var adapter = new NetEasePlatformLibraryAdapter(factory, await AuthorizedStoreAsync(), new PlatformJsonSerializer());
        var playlist = new PlatformUserPlaylist(PlatformId.NetEaseMusic, "900", "created", "歌单", "用户", "", 3);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.LoadPlaylistTracksAsync(playlist, TestContext.Current.CancellationToken));

        Assert.Equal("网易云音乐登录已失效，请重新登录", error.Message);
        Assert.DoesNotContain("301", error.Message, StringComparison.Ordinal);
    }

    private static FakeHttpMessageHandler Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) => new(callback);

    private static HttpResponseMessage Json(string value) => FakeHttpMessageHandler.Json(value, HttpStatusCode.OK);

    private static PlatformHttpClientFactory Factory(HttpMessageHandler handler) =>
        new(
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
            {
                ["qq"] = new FakeHttpMessageHandler((_, _) => Task.FromResult(Json("{}"))),
                ["netease"] = handler,
                ["kugou"] = new FakeHttpMessageHandler((_, _) => Task.FromResult(Json("{}")))
            },
            new CaptureSafeLogger(),
            new SensitiveDataRedactor(),
            new PlatformErrorMapper());

    private static async Task<MemoryCredentialStore> AuthorizedStoreAsync()
    {
        var store = new MemoryCredentialStore();
        await store.SaveAsync("netease", "session", "MUSIC_U=opaque; __csrf=csrf-value", TestContext.Current.CancellationToken);
        return store;
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 && pair[0] == name) return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }

    private sealed class MemoryCredentialStore : ISecureCredentialStore
    {
        private readonly Dictionary<(string Platform, string Name), string> _values = [];

        public Task SaveAsync(string platformId, string secretName, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[(platformId, secretName)] = value;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string platformId, string secretName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.GetValueOrDefault((platformId, secretName)));
        }

        public Task DeletePlatformAsync(string platformId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in _values.Keys.Where(key => key.Platform == platformId).ToArray()) _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
