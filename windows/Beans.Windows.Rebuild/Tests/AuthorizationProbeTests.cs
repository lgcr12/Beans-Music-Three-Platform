using System.Net;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Networking;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Beans.Windows.Rebuild.Tests.Infrastructure;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class AuthorizationProbeTests
{
    [Fact]
    public async Task QqProbeAcceptsProfileAndNeverPutsCookieInRequestKey()
    {
        string? requestUri = null;
        string? cookie = null;
        using var factory = QqDiscoveryTestInfrastructure.Factory(new FakeHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri?.ToString();
            cookie = request.Headers.GetValues("Cookie").Single();
            return Task.FromResult(FakeHttpMessageHandler.Json("{\"code\":0,\"data\":{\"nick\":\"user\"}}"));
        }));
        var probe = new QqAuthorizationProbe(factory, new PlatformJsonSerializer());

        var state = await probe.CheckAsync(
            new Dictionary<string, string> { ["session"] = "uin=o123456; qm_keyst=secret-key" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Valid, state);
        Assert.DoesNotContain("secret-key", requestUri, StringComparison.Ordinal);
        Assert.Equal("uin=o123456; qm_keyst=secret-key", cookie);
    }

    [Fact]
    public async Task QqProbeMapsRejectedProfileToExpired()
    {
        using var factory = QqDiscoveryTestInfrastructure.Factory(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{\"code\":-1,\"data\":null}"))));
        var probe = new QqAuthorizationProbe(factory, new PlatformJsonSerializer());

        var state = await probe.CheckAsync(
            new Dictionary<string, string> { ["session"] = "uin=o123456; qm_keyst=secret-key" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Expired, state);
    }

    [Fact]
    public async Task NetEaseProbeAcceptsAccountProfile()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(new FakeHttpMessageHandler((request, _) =>
        {
            Assert.DoesNotContain("MUSIC_U", request.RequestUri?.Query ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(FakeHttpMessageHandler.Json("{\"code\":200,\"profile\":{\"userId\":12345}}"));
        }));
        var probe = new NetEaseAuthorizationProbe(factory, new PlatformJsonSerializer());

        var state = await probe.CheckAsync(
            new Dictionary<string, string> { ["session"] = "MUSIC_U=opaque-user; __csrf=csrf" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Valid, state);
    }

    [Fact]
    public async Task NetEaseProbeRejectsUnauthorizedAccount()
    {
        using var factory = NetEaseDiscoveryTestInfrastructure.Factory(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Json("{\"code\":301,\"profile\":null}"))));
        var probe = new NetEaseAuthorizationProbe(factory, new PlatformJsonSerializer());

        var state = await probe.CheckAsync(
            new Dictionary<string, string> { ["session"] = "MUSIC_U=opaque-user" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Expired, state);
    }
}
