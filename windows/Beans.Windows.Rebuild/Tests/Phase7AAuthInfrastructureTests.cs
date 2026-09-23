using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;
using Beans.Windows.Rebuild.Services.Security;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class Phase7AAuthInfrastructureTests
{
    [Fact]
    public async Task UnsuccessfulAuthorizationNeverPersistsCredentialMaterial()
    {
        var store = new RecordingCredentialStore();
        var adapter = new TestAuthAdapter(
            PlatformId.QqMusic,
            new PlatformAuthorizationSession(
                PlatformId.QqMusic,
                CredentialState.Valid,
                new Dictionary<string, string> { ["session"] = "   " },
                null,
                "授权未完成"));
        using var service = new CredentialBackedPlatformAuthService(store, [adapter]);

        var result = await service.AuthorizeAsync(PlatformId.QqMusic, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(store.Saved);
        Assert.Equal(CredentialState.NotAuthorized, result.CredentialState);
    }

    [Fact]
    public async Task LogoutDeletesAllCredentialsForThePlatform()
    {
        var store = new RecordingCredentialStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.SaveAsync("qq", "session", "session-token", cancellationToken);
        await store.SaveAsync("qq", "refresh", "refresh-token", cancellationToken);
        await store.SaveAsync("netease", "session", "other-token", cancellationToken);
        using var service = new CredentialBackedPlatformAuthService(
            store,
            [new TestAuthAdapter(PlatformId.QqMusic, ValidSession, ["session", "refresh"])]);

        await service.LogoutAsync(PlatformId.QqMusic, cancellationToken);

        Assert.Empty(store.ValuesFor("qq"));
        Assert.Equal("other-token", await store.ReadAsync("netease", "session", cancellationToken));
        Assert.Equal(["qq"], store.DeletedPlatforms);
    }

    [Fact]
    public async Task StateReadsAllAdapterCredentialNames()
    {
        var store = new RecordingCredentialStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.SaveAsync("qq", "session", "session-token", cancellationToken);
        await store.SaveAsync("qq", "refresh", "refresh-token", cancellationToken);
        var adapter = new TestAuthAdapter(PlatformId.QqMusic, ValidSession, ["session", "refresh"]);
        using var service = new CredentialBackedPlatformAuthService(store, [adapter]);

        var state = await service.GetStateAsync(PlatformId.QqMusic, cancellationToken);

        Assert.Equal(CredentialState.Valid, state.CredentialState);
        Assert.Equal(["refresh", "session"], adapter.LastCheckedCredentials!.Keys.OrderBy(key => key).ToArray());
    }

    [Fact]
    public async Task CancellationPropagatesThroughStateAuthorizationAndLogout()
    {
        var store = new RecordingCredentialStore();
        var adapter = new TestAuthAdapter(PlatformId.QqMusic, ValidSession);
        using var service = new CredentialBackedPlatformAuthService(store, [adapter]);

        using (var stateCancellation = new CancellationTokenSource())
        {
            stateCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.GetStateAsync(PlatformId.QqMusic, stateCancellation.Token));
        }

        using (var authorizationCancellation = new CancellationTokenSource())
        {
            authorizationCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.AuthorizeAsync(PlatformId.QqMusic, authorizationCancellation.Token));
        }

        using (var logoutCancellation = new CancellationTokenSource())
        {
            logoutCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.LogoutAsync(PlatformId.QqMusic, logoutCancellation.Token));
        }
    }

    private static readonly PlatformAuthorizationSession ValidSession = new(
        PlatformId.QqMusic,
        CredentialState.Valid,
        new Dictionary<string, string>
        {
            ["session"] = "session-token",
            ["refresh"] = "refresh-token"
        },
        null,
        "授权成功");

    private sealed class TestAuthAdapter(
        PlatformId platform,
        PlatformAuthorizationSession session,
        IReadOnlyCollection<string>? credentialNames = null) : IPlatformAuthAdapter
    {
        public PlatformId Platform { get; } = platform;
        public IReadOnlyCollection<string> CredentialNames { get; } = credentialNames ?? ["session"];
        public IReadOnlyDictionary<string, string>? LastCheckedCredentials { get; private set; }

        public Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(session);
        }

        public Task<CredentialState> CheckAsync(
            IReadOnlyDictionary<string, string> credentials,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCheckedCredentials = credentials;
            return Task.FromResult(
                credentials.Count == session.Credentials.Count &&
                credentials.All(pair => session.Credentials.TryGetValue(pair.Key, out var expected) && expected == pair.Value)
                    ? CredentialState.Valid
                    : CredentialState.Expired);
        }
    }

    private sealed class RecordingCredentialStore : ISecureCredentialStore
    {
        private readonly Dictionary<(string Platform, string Name), string> _values = new();
        public List<(string Platform, string Name, string Value)> Saved { get; } = [];
        public List<string> DeletedPlatforms { get; } = [];

        public IReadOnlyDictionary<string, string> ValuesFor(string platform) =>
            _values.Where(pair => pair.Key.Platform == platform)
                .ToDictionary(pair => pair.Key.Name, pair => pair.Value, StringComparer.Ordinal);

        public Task SaveAsync(string platformId, string secretName, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[(platformId, secretName)] = value;
            Saved.Add((platformId, secretName, value));
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
            DeletedPlatforms.Add(platformId);
            foreach (var key in _values.Keys.Where(key => key.Platform == platformId).ToArray()) _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
