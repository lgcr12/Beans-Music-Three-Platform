using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Security;
using System.Collections.Concurrent;

namespace Beans.Windows.Rebuild.Services.Accounts;

public sealed record AuthorizationResult(
    PlatformId Platform,
    bool IsSuccess,
    CredentialState CredentialState,
    string SafeMessage);

public interface IPlatformAuthService
{
    Task<PlatformAccountState> GetStateAsync(PlatformId platform, CancellationToken cancellationToken);
    Task<AuthorizationResult> AuthorizeAsync(PlatformId platform, CancellationToken cancellationToken);
    Task LogoutAsync(PlatformId platform, CancellationToken cancellationToken);
}

/// <summary>
/// Platform-specific login implementations return short-lived credential material
/// to the shared coordinator. The coordinator is the only component allowed to
/// persist that material.
/// </summary>
public interface IPlatformAuthAdapter
{
    PlatformId Platform { get; }

    /// <summary>
    /// Names of the credential entries this adapter needs when checking a
    /// persisted session. Adapters that only use the default session value do
    /// not need to override this member.
    /// </summary>
    IReadOnlyCollection<string> CredentialNames => [CredentialBackedPlatformAuthService.DefaultCredentialName];

    Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken);
    Task<CredentialState> CheckAsync(IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken);
}

public sealed record PlatformAuthorizationSession(
    PlatformId Platform,
    CredentialState CredentialState,
    IReadOnlyDictionary<string, string> Credentials,
    DateTimeOffset? ExpiresAt,
    string SafeMessage);

/// <summary>
/// Shared authorization orchestration. Platform adapters never receive the
/// credential store and cannot persist cookies or tokens themselves.
/// </summary>
public sealed class CredentialBackedPlatformAuthService : IPlatformAuthService, IDisposable
{
    internal const string DefaultCredentialName = "session";
    private readonly ISecureCredentialStore _store;
    private readonly IReadOnlyDictionary<PlatformId, IPlatformAuthAdapter> _adapters;
    private readonly ConcurrentDictionary<PlatformId, SemaphoreSlim> _locks = new();

    public CredentialBackedPlatformAuthService(
        ISecureCredentialStore store,
        IEnumerable<IPlatformAuthAdapter> adapters)
    {
        _store = store;
        _adapters = adapters.ToDictionary(adapter => adapter.Platform);
    }

    public async Task<PlatformAccountState> GetStateAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_adapters.TryGetValue(platform, out var adapter))
            return State(platform, CredentialState.NotAuthorized, "当前平台尚未接入授权适配", canAuthorize: false);

        IReadOnlyDictionary<string, string> credentials;
        try
        {
            credentials = await ReadCredentialsAsync(platform, adapter, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return State(platform, CredentialState.Error, "登录状态暂时无法读取");
        }
        if (credentials.Count == 0)
            return State(platform, CredentialState.NotAuthorized, "尚未登录");

        try
        {
            var state = await adapter.CheckAsync(credentials, cancellationToken);
            return State(platform, state, Message(state));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return State(platform, CredentialState.Error, "登录状态暂时无法确认");
        }
    }

    public async Task<AuthorizationResult> AuthorizeAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_adapters.TryGetValue(platform, out var adapter))
            return Failure(platform, "当前平台尚未接入授权适配");

        var gate = _locks.GetOrAdd(platform, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var session = await adapter.AuthorizeAsync(cancellationToken);
            if (session is null)
                return new AuthorizationResult(
                    platform,
                    false,
                    CredentialState.Error,
                    "授权未完成");

            if (session.Platform != platform)
                return new AuthorizationResult(platform, false, CredentialState.Error, "授权平台不匹配");

            if (!HasUsableCredentials(session.Credentials))
            {
                var state = session.CredentialState is CredentialState.Error or CredentialState.Expired
                    ? session.CredentialState
                    : CredentialState.NotAuthorized;
                return new AuthorizationResult(platform, false, state, SafeMessage(session.SafeMessage, "授权未完成"));
            }

            if (session.CredentialState is not (CredentialState.Valid or CredentialState.Expiring))
                return new AuthorizationResult(platform, false, session.CredentialState, SafeMessage(session.SafeMessage, "授权未完成"));

            foreach (var credential in session.Credentials)
                await _store.SaveAsync(platform.ToStableId(), credential.Key, credential.Value, cancellationToken);

            return new AuthorizationResult(platform, true, session.CredentialState, SafeMessage(session.SafeMessage, "授权成功"));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return Failure(platform, "授权失败，请稍后重试");
        }
        finally { gate.Release(); }
    }

    public async Task LogoutAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = _locks.GetOrAdd(platform, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await _store.DeletePlatformAsync(platform.ToStableId(), cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadCredentialsAsync(
        PlatformId platform,
        IPlatformAuthAdapter adapter,
        CancellationToken cancellationToken)
    {
        var credentialNames = adapter.CredentialNames ?? [];
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in credentialNames.Where(IsUsableCredentialName).Distinct(StringComparer.Ordinal))
        {
            var value = await _store.ReadAsync(platform.ToStableId(), name, cancellationToken);
            if (!string.IsNullOrWhiteSpace(value)) credentials[name] = value;
        }

        return credentials;
    }

    private static PlatformAccountState State(PlatformId platform, CredentialState state, string message, bool canAuthorize = true) =>
        new(platform, state, platform.ToDisplayName(), message, DateTimeOffset.UtcNow, canAuthorize);

    private static AuthorizationResult Failure(PlatformId platform, string message) =>
        new(platform, false, CredentialState.Error, message);

    private static string Message(CredentialState state) => state switch
    {
        CredentialState.Valid => "已登录",
        CredentialState.Expiring => "登录即将过期",
        CredentialState.Expired => "登录已过期",
        CredentialState.Error => "登录状态异常",
        _ => "尚未登录"
    };

    private static bool HasUsableCredentials(IReadOnlyDictionary<string, string>? credentials) =>
        credentials is not null && credentials.Count > 0 &&
        credentials.All(pair => IsUsableCredentialName(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));

    private static bool IsUsableCredentialName(string? name) => !string.IsNullOrWhiteSpace(name);

    private static string SafeMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;

    public void Dispose()
    {
        foreach (var gate in _locks.Values) gate.Dispose();
        _locks.Clear();
    }
}

public sealed class PreviewPlatformAuthService : IPlatformAuthService
{
    public Task<PlatformAccountState> GetStateAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlatformAccountState(platform, CredentialState.NotAuthorized, platform.ToDisplayName(),
            "在线授权将在后续安全基础设施阶段接入", DateTimeOffset.UtcNow, true));
    }

    public Task<AuthorizationResult> AuthorizeAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AuthorizationResult(platform, false, CredentialState.NotAuthorized,
            "在线授权将在后续安全基础设施阶段接入"));
    }

    public Task LogoutAsync(PlatformId platform, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
