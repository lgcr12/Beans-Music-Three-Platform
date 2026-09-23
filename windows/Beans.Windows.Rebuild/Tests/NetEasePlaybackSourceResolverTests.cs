using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.Security;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEasePlaybackSourceResolverTests
{
    [Fact]
    public async Task MissingSessionReturnsAuthorizationBoundaryWithoutNetwork()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        using var factory = Factory(handler);
        var resolver = new NetEasePlaybackSourceResolver(factory, new MemoryCredentialStore(), new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.NetEaseMusic, "123")),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, result.Restriction);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task OfficialUrlResponseProducesPlaybackSourceAndSendsSessionCookie()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(FakeHttpMessageHandler.Json(
                "{\"code\":200,\"data\":[{\"id\":123,\"url\":\"https://m.music.example/audio.mp3\",\"br\":320000}]}"));
        });
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("netease", "session", "MUSIC_U=session-value", TestContext.Current.CancellationToken);
        var resolver = new NetEasePlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.NetEaseMusic, "123"), AudioQuality.VeryHigh),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://m.music.example/audio.mp3", result.Source!.Uri.AbsoluteUri);
        Assert.Equal(AudioQuality.VeryHigh, result.Source.Quality);
        Assert.Contains("MUSIC_U=session-value", captured!.Headers.GetValues("Cookie"));
        Assert.Contains("br=320000", captured.RequestUri!.Query);
    }

    [Fact]
    public async Task ProviderUnauthorizedResponseReturnsAuthorizationBoundary()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{\"code\":301,\"data\":[]}")));
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("netease", "session", "MUSIC_U=session-value", TestContext.Current.CancellationToken);
        var resolver = new NetEasePlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.NetEaseMusic, "123")),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, result.Restriction);
    }

    private static PlatformHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var handlers = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            ["qq"] = handler,
            ["netease"] = handler,
            ["kugou"] = handler
        };
        return new PlatformHttpClientFactory(handlers, new CaptureSafeLogger(), new SensitiveDataRedactor(), new PlatformErrorMapper());
    }

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
