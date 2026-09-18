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
