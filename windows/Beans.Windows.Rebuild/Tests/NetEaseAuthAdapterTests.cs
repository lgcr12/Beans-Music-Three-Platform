using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Platforms.NetEase;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class NetEaseAuthAdapterTests
{
    [Fact]
    public async Task DefaultAdapterDoesNotClaimAuthorization()
    {
        var adapter = new NetEaseMusicAuthAdapter();
        var result = await adapter.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PlatformId.NetEaseMusic, result.Platform);
        Assert.Equal(CredentialState.NotAuthorized, result.CredentialState);
        Assert.Empty(result.Credentials);
        Assert.Contains("登录窗口尚未接入", result.SafeMessage);
    }

    [Fact]
    public async Task InjectedFlowIsSanitizedBeforeReturningCredentials()
    {
        var adapter = new NetEaseMusicAuthAdapter(new FakeFlow(new PlatformAuthorizationSession(
            PlatformId.NetEaseMusic,
            CredentialState.Valid,
            new Dictionary<string, string>
            {
                ["session"] = "music-session",
                ["refresh"] = "refresh-token",
                ["unknown"] = "must-not-persist"
            },
            null,
            "授权完成")));

        var result = await adapter.AuthorizeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Credentials.ContainsKey("session"));
        Assert.True(result.Credentials.ContainsKey("refresh"));
        Assert.DoesNotContain("unknown", result.Credentials.Keys);
    }

    [Fact]
    public async Task MissingFlowDoesNotValidateOpaqueSession()
    {
        var adapter = new NetEaseMusicAuthAdapter();
        var state = await adapter.CheckAsync(
            new Dictionary<string, string> { ["session"] = "opaque" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CredentialState.Error, state);
    }

    private sealed class FakeFlow(PlatformAuthorizationSession session) : INetEaseAuthorizationFlow
    {
        public Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(session);
        }

        public Task<CredentialState> CheckAsync(IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken) =>
            Task.FromResult(CredentialState.Valid);
    }
}
