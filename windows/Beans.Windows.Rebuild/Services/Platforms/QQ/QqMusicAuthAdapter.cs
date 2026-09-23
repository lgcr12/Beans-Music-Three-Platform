using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

/// <summary>
/// Names used by the QQ authorization adapter. Values are opaque provider
/// session material and must only be passed to the secure credential store by
/// the shared authorization coordinator.
/// </summary>
public static class QqAuthCredentialNames
{
    public const string Session = "session";
    public const string Refresh = "refresh";

    internal static readonly IReadOnlyCollection<string> All = [Session, Refresh];
}

/// <summary>
/// The provider-owned UI/probe boundary for QQ authorization. A real
/// implementation can be supplied once the Windows WebView2 flow has an
/// exact qq.com allowlist and callback handling. It must never write secrets
/// itself; it returns short-lived material to the shared coordinator.
/// </summary>
public interface IQqAuthorizationFlow
{
    Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken);

    Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken);
}

/// <summary>
/// QQ Music authorization adapter.
///
/// The default instance is intentionally unsupported until a provider-domain
/// constrained WebView2/QR flow is available. It never claims authorization,
/// contacts a provider endpoint, or manufactures a credential. A concrete
/// QQ flow can be injected later without changing the shared auth contract.
/// </summary>
public sealed class QqMusicAuthAdapter : IPlatformAuthAdapter
{
    private const int MaxCredentialLength = 128 * 1024;
    private readonly IQqAuthorizationFlow? _flow;

    public QqMusicAuthAdapter(IQqAuthorizationFlow? flow = null)
    {
        _flow = flow;
    }

    public PlatformId Platform => PlatformId.QqMusic;

    public IReadOnlyCollection<string> CredentialNames => QqAuthCredentialNames.All;

    public async Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_flow is null)
            return UnsupportedSession();

        try
        {
            var session = await _flow.AuthorizeAsync(cancellationToken);
            return SanitizeSession(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Provider details and exception text must not escape the adapter.
            return ErrorSession("QQ 音乐授权暂时无法完成");
        }
    }

    public async Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeCredentials = SanitizeCredentials(credentials);
        if (safeCredentials.Count == 0)
            return CredentialState.NotAuthorized;

        // A persisted QQ cookie/session cannot be considered valid locally.
        // Without a provider-owned account probe, claiming Valid would make
        // expired or unrelated material look authorized.
        if (_flow is null)
            return CredentialState.Error;

        try
        {
            var state = await _flow.CheckAsync(safeCredentials, cancellationToken);
            return state is CredentialState.Valid or CredentialState.Expiring or CredentialState.Expired
                ? state
                : CredentialState.Error;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CredentialState.Error;
        }
    }

    private static PlatformAuthorizationSession SanitizeSession(PlatformAuthorizationSession? session)
    {
        if (session is null || session.Platform != PlatformId.QqMusic)
            return ErrorSession("QQ 音乐授权平台不匹配");

        var credentials = SanitizeCredentials(session.Credentials);
        if (credentials.Count == 0)
        {
            var state = session.CredentialState is CredentialState.Error or CredentialState.Expired
                ? session.CredentialState
                : CredentialState.NotAuthorized;
            return new PlatformAuthorizationSession(
                PlatformId.QqMusic,
                state,
                new Dictionary<string, string>(StringComparer.Ordinal),
                session.ExpiresAt,
                "QQ 音乐授权未完成");
        }

        // Only an explicitly authorized flow may produce a persisted session.
        if (session.CredentialState is not (CredentialState.Valid or CredentialState.Expiring))
            return new PlatformAuthorizationSession(
                PlatformId.QqMusic,
                session.CredentialState,
                new Dictionary<string, string>(StringComparer.Ordinal),
                session.ExpiresAt,
                "QQ 音乐授权未完成");

        return new PlatformAuthorizationSession(
            PlatformId.QqMusic,
            session.CredentialState,
            credentials,
            session.ExpiresAt,
            session.CredentialState == CredentialState.Expiring
                ? "QQ 音乐登录即将过期"
                : "QQ 音乐授权成功");
    }

    private static IReadOnlyDictionary<string, string> SanitizeCredentials(
        IReadOnlyDictionary<string, string>? credentials)
    {
        if (credentials is null || credentials.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in QqAuthCredentialNames.All)
        {
            if (!credentials.TryGetValue(name, out var value) || !IsSafeCredential(value))
                continue;
            safe[name] = value;
        }

        return safe;
    }

    private static bool IsSafeCredential(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxCredentialLength &&
        value.All(character => !char.IsControl(character));

    private static PlatformAuthorizationSession UnsupportedSession() =>
        new(
            PlatformId.QqMusic,
            CredentialState.NotAuthorized,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            "QQ 音乐安全登录窗口尚未接入");

    private static PlatformAuthorizationSession ErrorSession(string message) =>
        new(
            PlatformId.QqMusic,
            CredentialState.Error,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            message);

}
