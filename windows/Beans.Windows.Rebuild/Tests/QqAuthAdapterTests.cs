using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Platforms.QQ;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class QqAuthAdapterTests
{
    [Fact]
    public async Task DefaultAdapterIsExplicitlyUnsupportedAndNeverReturnsCredentials()
    {
        var adapter = new QqMusicAuthAdapter();

        var session = await adapter.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PlatformId.QqMusic, adapter.Platform);
        Assert.Equal(["session", "refresh"], adapter.CredentialNames);
        Assert.Equal(CredentialState.NotAuthorized, session.CredentialState);
        Assert.Empty(session.Credentials);
        Assert.Contains("安全登录窗口", session.SafeMessage);
    }

    [Fact]
    public async Task DefaultAdapterDoesNotTreatPersistedOpaqueMaterialAsValid()
    {
        var adapter = new QqMusicAuthAdapter();

        var state = await adapter.CheckAsync(
            new Dictionary<string, string> { [QqAuthCredentialNames.Session] = "opaque-cookie" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Error, state);
    }

    [Fact]
    public async Task EmptyOrUnknownCredentialsRemainNotAuthorized()
    {
        var adapter = new QqMusicAuthAdapter();

        var empty = await adapter.CheckAsync(
            new Dictionary<string, string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);
        var unknown = await adapter.CheckAsync(
            new Dictionary<string, string> { ["access_token"] = "do-not-store" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.NotAuthorized, empty);
        Assert.Equal(CredentialState.NotAuthorized, unknown);
    }

    [Fact]
    public async Task InjectedFlowIsSanitizedBeforeItCanReachTheSharedCoordinator()
    {
        var flow = new FakeQqAuthorizationFlow
        {
            Session = new PlatformAuthorizationSession(
                PlatformId.QqMusic,
                CredentialState.Valid,
                new Dictionary<string, string>
                {
                    [QqAuthCredentialNames.Session] = "session-value",
                    [QqAuthCredentialNames.Refresh] = "refresh\nvalue",
                    ["access_token"] = "unknown-value"
                },
                null,
                "登录成功")
        };
        var adapter = new QqMusicAuthAdapter(flow);

        var session = await adapter.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Valid, session.CredentialState);
        Assert.Equal([QqAuthCredentialNames.Session], session.Credentials.Keys);
        Assert.Equal("session-value", session.Credentials[QqAuthCredentialNames.Session]);
        Assert.DoesNotContain("access_token", session.Credentials.Keys);
    }

    [Fact]
    public async Task InvalidPlatformOrInvalidStateCannotPersistCredentials()
    {
        var flow = new FakeQqAuthorizationFlow
        {
            Session = new PlatformAuthorizationSession(
                PlatformId.NetEaseMusic,
                CredentialState.Valid,
                new Dictionary<string, string> { [QqAuthCredentialNames.Session] = "value" },
                null,
                "provider message")
        };
        var adapter = new QqMusicAuthAdapter(flow);

        var session = await adapter.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PlatformId.QqMusic, session.Platform);
        Assert.Equal(CredentialState.Error, session.CredentialState);
        Assert.Empty(session.Credentials);
        Assert.Contains("平台不匹配", session.SafeMessage);
    }

    [Fact]
    public async Task FlowExceptionsBecomeSafeErrorWithoutLeakingExceptionText()
    {
        var flow = new FakeQqAuthorizationFlow { ThrowOnAuthorize = true };
        var adapter = new QqMusicAuthAdapter(flow);

        var session = await adapter.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Error, session.CredentialState);
        Assert.Empty(session.Credentials);
        Assert.Equal("QQ 音乐授权暂时无法完成", session.SafeMessage);
        Assert.DoesNotContain("secret", session.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeUnsupportedResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new QqMusicAuthAdapter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.AuthorizeAsync(cancellation.Token));
    }

    private sealed class FakeQqAuthorizationFlow : IQqAuthorizationFlow
    {
        public PlatformAuthorizationSession? Session { get; init; }
        public bool ThrowOnAuthorize { get; init; }

        public Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnAuthorize)
                throw new InvalidOperationException("secret provider detail");
            return Task.FromResult(Session ?? new PlatformAuthorizationSession(
                PlatformId.QqMusic,
                CredentialState.NotAuthorized,
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                "未完成"));
        }

        public Task<CredentialState> CheckAsync(
            IReadOnlyDictionary<string, string> credentials,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(credentials.Count == 0 ? CredentialState.NotAuthorized : CredentialState.Valid);
        }
    }
}
