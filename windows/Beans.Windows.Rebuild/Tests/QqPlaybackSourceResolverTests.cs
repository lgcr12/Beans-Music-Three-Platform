using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Playback;
using Beans.Windows.Rebuild.Services.Security;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using System.Net;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqPlaybackSourceResolverTests
{
    [Fact]
    public async Task MissingSessionReturnsAuthorizationBoundaryWithoutNetwork()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        using var factory = Factory(handler);
        var resolver = new QqPlaybackSourceResolver(factory, new MemoryCredentialStore(), new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid")),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, result.Restriction);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task OfficialVkeyResponseProducesPlaybackSource()
    {
        Uri? capturedUri = null;
        string? capturedCookie = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host.Equals("u.y.qq.com", StringComparison.OrdinalIgnoreCase))
            {
                capturedUri = request.RequestUri;
                capturedCookie = request.Headers.GetValues("Cookie").Single();
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent));
            }
            return Task.FromResult(FakeHttpMessageHandler.Json(
                "{\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[{\"purl\":\"C400media-mid.m4a\",\"type\":\"C400\"}],\"sip\":[\"https://isure.stream.qqmusic.qq.com/\"]}}}"));
        });
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=12345; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid"), AudioQuality.VeryHigh,
                ProviderMediaId: "media-mid"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://isure.stream.qqmusic.qq.com/C400media-mid.m4a", result.Source!.Uri.AbsoluteUri);
        Assert.Equal(AudioQuality.VeryHigh, result.Source.Quality);
        Assert.Contains("qm_keyst=login-key", capturedCookie);
        Assert.Contains("data=", capturedUri!.Query);
        var decoded = Uri.UnescapeDataString(capturedUri.Query);
        Assert.Contains("\"g_tk\":", decoded, StringComparison.Ordinal);
        Assert.Contains("\"authst\":\"login-key\"", decoded, StringComparison.Ordinal);
        Assert.Contains("C400media-mid.m4a", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProviderMediaIdUsesOfficialSongDetailBeforeVkey()
    {
        var requests = new List<(HttpMethod Method, Uri Uri)>();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requests.Add((request.Method, request.RequestUri!));
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(FakeHttpMessageHandler.Json(
                    "{\"req_0\":{\"data\":{\"track_info\":{\"file\":{\"media_mid\":\"resolved-media\"}}}}}"));
            return Task.FromResult(FakeHttpMessageHandler.Json(
                "{\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[{\"purl\":\"M500resolved-media.mp3\",\"result\":0}],\"sip\":[\"https://dl.stream.qqmusic.qq.com/\"]}}}"));
        });
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=12345; p_skey=gtk-key; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid")),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Contains("M500resolved-media.mp3", Uri.UnescapeDataString(requests[1].Uri.Query));
        Assert.Equal("dl.stream.qqmusic.qq.com", requests[2].Uri.Host);
    }

    [Fact]
    public async Task CdnProbeFallsBackToSecondOfficialHostWithoutReadingBody()
    {
        var probeHosts = new List<string>();
        var successfulContent = new PoisonContent();
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host.Equals("u.y.qq.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(FakeHttpMessageHandler.Json(
                    "{\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[{\"purl\":\"M500media-mid.mp3\",\"result\":0}],\"sip\":[\"https://first.stream.qqmusic.qq.com/\",\"https://second.stream.qqmusic.qq.com/\"]}}}"));

            probeHosts.Add(request.RequestUri.Host);
            Assert.Equal(0, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal(1, request.Headers.Range.Ranges.Single().To);
            Assert.Equal("identity", request.Headers.GetValues("Accept-Encoding").Single());
            Assert.Equal("https://y.qq.com/", request.Headers.Referrer!.AbsoluteUri);
            Assert.Contains("qm_keyst=login-key", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(request.RequestUri.Host.StartsWith("first", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = successfulContent });
        });
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=12345; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid"), ProviderMediaId: "media-mid"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("second.stream.qqmusic.qq.com", result.Source!.Uri.Host);
        Assert.Equal(["first.stream.qqmusic.qq.com", "second.stream.qqmusic.qq.com"], probeHosts);
        Assert.False(successfulContent.WasRead);
    }

    [Fact]
    public async Task AllOfficialCdnCandidatesFailSafely()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host.Equals("u.y.qq.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(FakeHttpMessageHandler.Json(
                    "{\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[{\"purl\":\"M500media-mid.mp3\",\"result\":0}],\"sip\":[\"https://first.stream.qqmusic.qq.com/\",\"https://second.stream.qqmusic.qq.com/\"]}}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=12345; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(
                new MusicIdentity(PlatformId.QqMusic, "song-mid"),
                AudioQuality.Standard,
                ProviderMediaId: "media-mid"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackRestriction.FileUnavailable, result.Restriction);
    }

    [Theory]
    [InlineData(104003, "vip required", PlaybackRestriction.SubscriptionRequired)]
    [InlineData(0, "region restricted", PlaybackRestriction.RegionRestricted)]
    [InlineData(0, "file unavailable", PlaybackRestriction.FileUnavailable)]
    public async Task EmptyVkeyMapsSafeRestriction(int resultCode, string message, PlaybackRestriction expected)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            req_0 = new { code = 0, data = new { midurlinfo = new[] { new { purl = "", result = resultCode, msg = message } } } }
        });
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(body)));
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=12345; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var resolved = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid"), ProviderMediaId: "media-mid"),
            TestContext.Current.CancellationToken);

        Assert.False(resolved.IsSuccess);
        Assert.Equal(expected, resolved.Restriction);
    }

    [Fact]
    public async Task VkeyCannotRedirectPlaybackToUntrustedHost()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeHttpMessageHandler.Json(
            "{\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[{\"purl\":\"https://untrusted.example/audio.mp3\",\"result\":0}]}}}")));
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=12345; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var resolved = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid"), ProviderMediaId: "media-mid"),
            TestContext.Current.CancellationToken);

        Assert.False(resolved.IsSuccess);
        Assert.Equal(PlaybackRestriction.ProviderUnavailable, resolved.Restriction);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task MalformedSessionReturnsAuthorizationBoundary()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{}")));
        using var factory = Factory(handler);
        var store = new MemoryCredentialStore();
        await store.SaveAsync("qq", "session", "uin=not-a-number; qm_keyst=login-key", TestContext.Current.CancellationToken);
        var resolver = new QqPlaybackSourceResolver(factory, store, new PlatformJsonSerializer());

        var result = await resolver.ResolveAsync(
            new PlaybackSourceRequest(new MusicIdentity(PlatformId.QqMusic, "song-mid")),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlaybackRestriction.RequiresAuthorization, result.Restriction);
        Assert.Equal(0, handler.SendCount);
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

    private sealed class PoisonContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("CDN probe must not read the response body.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 2;
            return true;
        }
    }
}
