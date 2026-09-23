using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Security;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PlatformAuthServiceTests
{
    [Fact]
    public async Task AuthorizationPersistsOnlyThroughSecureStoreAndCanLogout()
    {
        var store = new MemoryCredentialStore();
        var adapter = new FakeAuthAdapter(PlatformId.QqMusic);
        using var service = new CredentialBackedPlatformAuthService(store, [adapter]);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await service.AuthorizeAsync(PlatformId.QqMusic, cancellationToken);
        var state = await service.GetStateAsync(PlatformId.QqMusic, cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(CredentialState.Valid, state.CredentialState);
        Assert.Equal("session-token", await store.ReadAsync("qq", "session", cancellationToken));

        await service.LogoutAsync(PlatformId.QqMusic, cancellationToken);

        var loggedOut = await service.GetStateAsync(PlatformId.QqMusic, cancellationToken);
        Assert.Equal(CredentialState.NotAuthorized, loggedOut.CredentialState);
        Assert.Null(await store.ReadAsync("qq", "session", cancellationToken));
    }

    [Fact]
    public async Task MissingAdapterDoesNotClaimAuthorization()
    {
        using var service = new CredentialBackedPlatformAuthService(new MemoryCredentialStore(), []);

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await service.AuthorizeAsync(PlatformId.NetEaseMusic, cancellationToken);
        var state = await service.GetStateAsync(PlatformId.NetEaseMusic, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(CredentialState.Error, result.CredentialState);
        Assert.Equal(CredentialState.NotAuthorized, state.CredentialState);
        Assert.Contains("尚未接入", result.SafeMessage);
    }

    private sealed class FakeAuthAdapter(PlatformId platform) : IPlatformAuthAdapter
    {
        public PlatformId Platform { get; } = platform;

        public Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PlatformAuthorizationSession(
                Platform,
                CredentialState.Valid,
                new Dictionary<string, string> { ["session"] = "session-token" },
                null,
                "授权成功"));
        }

        public Task<CredentialState> CheckAsync(IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(credentials.TryGetValue("session", out var value) && value == "session-token"
                ? CredentialState.Valid
                : CredentialState.Expired);
        }
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
