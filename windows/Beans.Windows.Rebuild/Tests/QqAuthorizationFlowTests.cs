using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqAuthorizationFlowTests
{
    [Fact]
    public void ProviderPolicyAcceptsOnlyExactHttpsHosts()
    {
        Assert.True(QqProviderDomainPolicy.IsAllowedHttps(new Uri("https://y.qq.com/login")));
        Assert.True(QqProviderDomainPolicy.IsAllowedHttps(new Uri("https://ssl.ptlogin2.qq.com/check")));
        Assert.False(QqProviderDomainPolicy.IsAllowedHttps(new Uri("http://y.qq.com/login")));
        Assert.False(QqProviderDomainPolicy.IsAllowedHttps(new Uri("https://evil.qq.com/login")));
        Assert.False(QqProviderDomainPolicy.IsAllowedHttps(new Uri("https://y.qq.com.evil.example/login")));
        Assert.False(QqProviderDomainPolicy.IsAllowedHttps(new Uri("https://y.qq.com:444/login")));
    }

    [Fact]
    public void CallbackRequiresAllowedOriginStateAndCode()
    {
        var uri = new Uri("https://y.qq.com/callback?code=one-time-code&state=expected");

        Assert.True(QqAuthorizationCallbackExtractor.TryExtract(uri, "expected", out var callback));
        Assert.True(callback.IsValid);
        Assert.Equal("one-time-code", callback.Code);
        Assert.DoesNotContain("one-time-code", callback.SafeMessage, StringComparison.Ordinal);

        Assert.False(QqAuthorizationCallbackExtractor.TryExtract(uri, "wrong", out var mismatch));
        Assert.Contains("状态", mismatch.SafeMessage, StringComparison.Ordinal);
        Assert.False(QqAuthorizationCallbackExtractor.TryExtract(
            new Uri("https://evil.example/callback?code=one&state=expected"), "expected", out _));
    }

    [Fact]
    public void CookieExtractorDropsUnrelatedValuesAndRequiresIdentityAndKey()
    {
        var cookies = new Dictionary<string, string>
        {
            ["uin"] = "o123456",
            ["qm_keyst"] = "login-key",
            ["tracking"] = "must-not-persist",
            ["bad"] = "line\nvalue"
        };

        Assert.True(QqAuthorizationCookieExtractor.TryExtractSession(cookies, out var session));
        Assert.Contains("uin=o123456", session, StringComparison.Ordinal);
        Assert.Contains("qm_keyst=login-key", session, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking", session, StringComparison.Ordinal);
        Assert.DoesNotContain("bad", session, StringComparison.Ordinal);

        Assert.False(QqAuthorizationCookieExtractor.TryExtractSession(
            new Dictionary<string, string> { ["uin"] = "o123456" }, out _));
        Assert.False(QqAuthorizationCookieExtractor.TryExtractSession(
            new Dictionary<string, string> { ["uin"] = "not-a-number", ["skey"] = "value" }, out _));
    }

    [Fact]
    public async Task FlowRequiresProbeBeforeReturningValidAndNeverStoresCallbackCode()
    {
        var browser = new FakeBrowser
        {
            Cookies = new Dictionary<string, string>
            {
                ["uin"] = "o123456",
                ["qm_keyst"] = "login-key",
                ["tracking"] = "ignored"
            }
        };
        var probe = new FakeProbe { Result = CredentialState.Valid };
        var flow = new QqAuthorizationFlow(browser, probe);

        var session = await flow.AuthorizeAsync(CancellationToken.None);

        Assert.Equal(PlatformId.QqMusic, session.Platform);
        Assert.Equal(CredentialState.Valid, session.CredentialState);
        Assert.Single(session.Credentials);
        Assert.Contains("uin=o123456", session.Credentials[QqAuthCredentialNames.Session], StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-code", session.Credentials[QqAuthCredentialNames.Session], StringComparison.Ordinal);
        Assert.NotNull(browser.Request);
        Assert.Contains("ssl.ptlogin2.qq.com", browser.Request!.AllowedHosts, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.qq.com", browser.Request.AllowedHosts, StringComparer.OrdinalIgnoreCase);
        Assert.Single(probe.CredentialsSeen);
    }

    [Fact]
    public async Task FlowDoesNotPersistCookiesWhenProbeRejectsSession()
    {
        var browser = new FakeBrowser
        {
            Cookies = new Dictionary<string, string>
            {
                ["uin"] = "o123456",
                ["skey"] = "login-key"
            }
        };
        var flow = new QqAuthorizationFlow(browser, new FakeProbe { Result = CredentialState.Expired });

        var session = await flow.AuthorizeAsync(CancellationToken.None);

        Assert.Equal(CredentialState.Expired, session.CredentialState);
        Assert.Empty(session.Credentials);
    }

    [Fact]
    public async Task CookieBearingOfficialPageCanCompleteWithoutSyntheticOAuthCallback()
    {
        var browser = new FakeBrowser
        {
            CompletedUriFactory = _ => new Uri("https://y.qq.com/portal/profile.html"),
            Cookies = new Dictionary<string, string>
            {
                ["uin"] = "o123456",
                ["qm_keyst"] = "login-key"
            }
        };

        var session = await new QqAuthorizationFlow(browser, new FakeProbe { Result = CredentialState.Valid })
            .AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Valid, session.CredentialState);
        Assert.Single(session.Credentials);
    }

    private sealed class FakeBrowser : IQqAuthorizationBrowser
    {
        public QqAuthorizationBrowserRequest? Request { get; private set; }
        public IReadOnlyDictionary<string, string> Cookies { get; init; } = new Dictionary<string, string>();
        public Func<QqAuthorizationBrowserRequest, Uri>? CompletedUriFactory { get; init; }

        public Task<QqAuthorizationBrowserCapture> CaptureAsync(
            QqAuthorizationBrowserRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            var callback = CompletedUriFactory?.Invoke(request) ??
                new Uri($"https://y.qq.com/callback?code=one-time-code&state={Uri.EscapeDataString(request.State)}");
            return Task.FromResult(new QqAuthorizationBrowserCapture(callback, Cookies));
        }
    }

    private sealed class FakeProbe : IQqAuthorizationProbe
    {
        public CredentialState Result { get; init; }
        public List<IReadOnlyDictionary<string, string>> CredentialsSeen { get; } = [];

        public Task<CredentialState> CheckAsync(
            IReadOnlyDictionary<string, string> credentials,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CredentialsSeen.Add(credentials);
            return Task.FromResult(Result);
        }
    }
}
