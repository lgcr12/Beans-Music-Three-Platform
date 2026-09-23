using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public static class NetEaseAuthCredentialNames
{
    public const string Session = "session";
    public const string Refresh = "refresh";
    internal static readonly IReadOnlyCollection<string> All = [Session, Refresh];
}

/// <summary>
/// Provider-owned WebView2/QR boundary for NetEase authorization. It is kept
/// separate from the shared coordinator so cookies never reach ordinary UI or
/// application files.
/// </summary>
public interface INetEaseAuthorizationFlow
{
    Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken);
    Task<CredentialState> CheckAsync(IReadOnlyDictionary<string, string> credentials, CancellationToken cancellationToken);
}

/// <summary>
/// NetEase authorization adapter. The default instance is deliberately
/// unsupported until a real music.163.com allowlisted login flow is supplied.
/// </summary>
public sealed class NetEaseMusicAuthAdapter : IPlatformAuthAdapter
{
    private const int MaxCredentialLength = 128 * 1024;
    private readonly INetEaseAuthorizationFlow? _flow;

    public NetEaseMusicAuthAdapter(INetEaseAuthorizationFlow? flow = null) => _flow = flow;

    public PlatformId Platform => PlatformId.NetEaseMusic;
    public IReadOnlyCollection<string> CredentialNames => NetEaseAuthCredentialNames.All;

    public async Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_flow is null)
            return UnsupportedSession();

        try
        {
            return SanitizeSession(await _flow.AuthorizeAsync(cancellationToken));
        }
        catch (OperationCanceledException) { throw; }
        catch { return ErrorSession("网易云音乐授权暂时无法完成"); }
    }

    public async Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeCredentials = SanitizeCredentials(credentials);
        if (safeCredentials.Count == 0) return CredentialState.NotAuthorized;
        if (_flow is null) return CredentialState.Error;

        try
        {
            var state = await _flow.CheckAsync(safeCredentials, cancellationToken);
            return state is CredentialState.Valid or CredentialState.Expiring or CredentialState.Expired
                ? state
                : CredentialState.Error;
        }
        catch (OperationCanceledException) { throw; }
        catch { return CredentialState.Error; }
    }

    private static PlatformAuthorizationSession SanitizeSession(PlatformAuthorizationSession? session)
    {
        if (session is null || session.Platform != PlatformId.NetEaseMusic)
            return ErrorSession("网易云音乐授权平台不匹配");

        var credentials = SanitizeCredentials(session.Credentials);
        if (credentials.Count == 0 || session.CredentialState is not (CredentialState.Valid or CredentialState.Expiring))
            return new PlatformAuthorizationSession(
                PlatformId.NetEaseMusic,
                session.CredentialState is CredentialState.Error or CredentialState.Expired
                    ? session.CredentialState
                    : CredentialState.NotAuthorized,
                new Dictionary<string, string>(StringComparer.Ordinal),
                session.ExpiresAt,
                SafeMessage(session.SafeMessage, "网易云音乐授权未完成"));

        return new PlatformAuthorizationSession(
            PlatformId.NetEaseMusic,
            session.CredentialState,
            credentials,
            session.ExpiresAt,
            SafeMessage(session.SafeMessage, "网易云音乐授权成功"));
    }

    private static IReadOnlyDictionary<string, string> SanitizeCredentials(IReadOnlyDictionary<string, string>? credentials)
    {
        if (credentials is null || credentials.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in NetEaseAuthCredentialNames.All)
        {
            if (credentials.TryGetValue(name, out var value) && IsSafeCredential(value)) safe[name] = value;
        }
        return safe;
    }

    private static bool IsSafeCredential(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxCredentialLength && value.All(character => !char.IsControl(character));

    private static PlatformAuthorizationSession UnsupportedSession() => new(
        PlatformId.NetEaseMusic,
        CredentialState.NotAuthorized,
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        "网易云音乐安全登录窗口尚未接入");

    private static PlatformAuthorizationSession ErrorSession(string message) => new(
        PlatformId.NetEaseMusic,
        CredentialState.Error,
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        message);

    private static string SafeMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
}
