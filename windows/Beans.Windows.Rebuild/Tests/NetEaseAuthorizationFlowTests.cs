using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEaseAuthorizationFlowTests
{
    [Theory]
    [InlineData("https://music.163.com/")]
    [InlineData("https://music.163.com/#/login")]
    public void ProviderPolicyAllowsOnlyExactHttpsHost(string value)
    {
        Assert.True(NetEaseProviderDomainPolicy.IsAllowedHttps(new Uri(value)));
        Assert.False(NetEaseProviderDomainPolicy.IsAllowedHttps(new Uri("http://music.163.com/")));
        Assert.False(NetEaseProviderDomainPolicy.IsAllowedHttps(new Uri("https://music.163.com.attacker.example/")));
        Assert.False(NetEaseProviderDomainPolicy.IsAllowedHttps(new Uri("https://music.163.com:444/")));
        Assert.False(NetEaseProviderDomainPolicy.IsAllowedHttps(new Uri("https://user:pass@music.163.com/")));
    }

    [Fact]
    public void OptionalCallbackStateMustMatchFlowNonce()
    {
        Assert.True(NetEaseAuthorizationCallbackValidator.IsValid(
            new Uri("https://music.163.com/#/login"), "expected"));
        Assert.True(NetEaseAuthorizationCallbackValidator.IsValid(
            new Uri("https://music.163.com/callback?state=expected"), "expected"));
        Assert.False(NetEaseAuthorizationCallbackValidator.IsValid(
            new Uri("https://music.163.com/callback?state=wrong"), "expected"));
        Assert.False(NetEaseAuthorizationCallbackValidator.IsValid(
            new Uri("https://music.163.com.attacker.example/callback?state=expected"), "expected"));
    }

    [Fact]
    public void CookieExtractorKeepsOnlySafeAccountCookies()
    {
        var cookies = new Dictionary<string, string>
        {
            ["MUSIC_U"] = "account-session",
            ["MUSIC_A"] = "account-token",
            ["__csrf"] = "csrf",
            ["tracking"] = "must-not-leave",
            ["NMTID"] = "bad;cookie"
        };

        Assert.True(NetEaseAuthorizationCookieExtractor.TryExtractSession(cookies, out var session));
        Assert.Equal("MUSIC_U=account-session; MUSIC_A=account-token; __csrf=csrf", session);
        Assert.DoesNotContain("tracking", session);
        Assert.DoesNotContain("bad;cookie", session);
    }

    [Fact]
    public async Task FlowRequiresAllowedCompletionAndProviderProbeBeforeReturningCredentials()
    {
        var browser = new FakeBrowser
        {
            Capture = new NetEaseAuthorizationBrowserCapture(
                new Uri("https://music.163.com/#/login"),
                new Dictionary<string, string>
                {
                    ["MUSIC_U"] = "account-session",
                    ["MUSIC_A"] = "account-token",
                    ["tracking"] = "discard"
                })
        };
        var probe = new FakeProbe { State = CredentialState.Valid };
        var flow = new NetEaseAuthorizationFlow(browser, probe);

        var result = await flow.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PlatformId.NetEaseMusic, result.Platform);
        Assert.Equal(CredentialState.Valid, result.CredentialState);
        Assert.Equal("MUSIC_U=account-session; MUSIC_A=account-token", result.Credentials[NetEaseAuthCredentialNames.Session]);
        Assert.Equal(1, probe.CallCount);
        Assert.NotNull(browser.Request);
        Assert.Equal("https://music.163.com/", browser.Request!.LoginUri.ToString());
        Assert.Contains("music.163.com", browser.Request.AllowedHosts);
        Assert.DoesNotContain("tracking", browser.Request.AllowedCookieNames);
    }

    [Fact]
    public async Task FlowRejectsUntrustedCompletionWithoutCallingProbe()
    {
        var browser = new FakeBrowser
        {
            Capture = new NetEaseAuthorizationBrowserCapture(
                new Uri("https://music.163.com.attacker.example/"),
                new Dictionary<string, string> { ["MUSIC_U"] = "account-session" })
        };
        var probe = new FakeProbe { State = CredentialState.Valid };
        var result = await new NetEaseAuthorizationFlow(browser, probe)
            .AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.NotAuthorized, result.CredentialState);
        Assert.Empty(result.Credentials);
        Assert.Contains("不受信任", result.SafeMessage);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task ProbeFailureDoesNotReturnSessionMaterial()
    {
        var browser = new FakeBrowser
        {
            Capture = new NetEaseAuthorizationBrowserCapture(
                new Uri("https://music.163.com/"),
                new Dictionary<string, string> { ["MUSIC_U"] = "account-session" })
        };
        var probe = new FakeProbe { State = CredentialState.Error };

        var result = await new NetEaseAuthorizationFlow(browser, probe)
            .AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Error, result.CredentialState);
        Assert.Empty(result.Credentials);
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeBrowserInvocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var browser = new FakeBrowser();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NetEaseAuthorizationFlow(browser, new FakeProbe())
                .AuthorizeAsync(cancellation.Token));
        Assert.Null(browser.Request);
    }

    [Fact]
    public async Task CheckRejectsMalformedSessionWithoutCallingProbe()
    {
        var probe = new FakeProbe { State = CredentialState.Valid };
        var flow = new NetEaseAuthorizationFlow(new FakeBrowser(), probe);

        var state = await flow.CheckAsync(
            new Dictionary<string, string>
            {
                [NetEaseAuthCredentialNames.Session] = "MUSIC_A=anonymous"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.NotAuthorized, state);
        Assert.Equal(0, probe.CallCount);
    }

    private sealed class FakeBrowser : INetEaseAuthorizationBrowser
    {
        public NetEaseAuthorizationBrowserCapture? Capture { get; init; }
        public NetEaseAuthorizationBrowserRequest? Request { get; private set; }

        public Task<NetEaseAuthorizationBrowserCapture> CaptureAsync(
            NetEaseAuthorizationBrowserRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(Capture ?? new NetEaseAuthorizationBrowserCapture(null, null, true));
        }
    }

    private sealed class FakeProbe : INetEaseAuthorizationProbe
    {
        public CredentialState State { get; init; } = CredentialState.NotAuthorized;
        public int CallCount { get; private set; }

        public Task<CredentialState> CheckAsync(
            IReadOnlyDictionary<string, string> credentials,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(State);
        }
    }
}
