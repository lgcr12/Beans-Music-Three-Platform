using System.Globalization;
using System.Security.Cryptography;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

/// <summary>
/// The small surface a WebView2 host needs to implement for QQ login. The
/// host owns the browser instance and its isolated profile; this contract does
/// not expose WebView2 types so the platform flow remains unit-testable.
/// </summary>
public interface IQqAuthorizationBrowser
{
    Task<QqAuthorizationBrowserCapture> CaptureAsync(
        QqAuthorizationBrowserRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider account probe. A successful browser callback is not sufficient to
/// persist a session: the probe must confirm that the filtered cookie session
/// is accepted by QQ before the flow returns Valid or Expiring.
/// </summary>
public interface IQqAuthorizationProbe
{
    Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken);
}

public sealed record QqAuthorizationBrowserRequest(
    Uri LoginUri,
    string State,
    IReadOnlyCollection<string> AllowedHosts,
    IReadOnlyCollection<string> AllowedCookieNames);

public sealed record QqAuthorizationBrowserCapture(
    Uri? CompletedUri,
    IReadOnlyDictionary<string, string>? Cookies,
    bool IsCancelled = false);

/// <summary>
/// QQ's browser-domain policy. Keep this list exact; do not replace it with a
/// suffix check because an attacker-controlled qq.com look-alike must never
/// receive the isolated login profile or its cookies.
/// </summary>
public static class QqProviderDomainPolicy
{
    private static readonly string[] Hosts =
    [
        "y.qq.com",
        "c.y.qq.com",
        "u.y.qq.com",
        "i.y.qq.com",
        "ssl.ptlogin2.qq.com",
        "xui.ptlogin2.qq.com",
        "ptlogin2.qq.com"
    ];
    private static readonly IReadOnlyCollection<string> ReadOnlyHosts = Array.AsReadOnly(Hosts);

    public static IReadOnlyCollection<string> AllowedHosts => ReadOnlyHosts;

    public static bool IsAllowedHttps(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || (uri.Port is not (-1 or 443)))
            return false;

        var host = uri.DnsSafeHost;
        return Hosts.Any(value => value.Equals(host, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Values returned from the callback are intentionally transient. The flow
/// never treats an OAuth code as a credential and never writes callback URLs
/// or cookie material to logs.
/// </summary>
public sealed record QqAuthorizationCallback(
    bool IsValid,
    string? Code,
    string? ReturnedState,
    string? Error,
    string SafeMessage);

public static class QqAuthorizationCallbackExtractor
{
    private const int MaxComponentLength = 16 * 1024;

    public static bool TryExtract(
        Uri? callbackUri,
        string expectedState,
        out QqAuthorizationCallback callback)
    {
        callback = new QqAuthorizationCallback(false, null, null, null, "QQ 音乐回调无效");
        if (!QqProviderDomainPolicy.IsAllowedHttps(callbackUri) || string.IsNullOrWhiteSpace(expectedState))
            return false;

        var values = ParseParameters(callbackUri!.Query, callbackUri.Fragment);
        values.TryGetValue("state", out var returnedState);
        values.TryGetValue("code", out var code);
        values.TryGetValue("error", out var error);

        if (string.IsNullOrWhiteSpace(returnedState) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expectedState),
                System.Text.Encoding.UTF8.GetBytes(returnedState)))
        {
            callback = new QqAuthorizationCallback(false, null, returnedState, null, "QQ 音乐登录状态校验失败");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            callback = new QqAuthorizationCallback(false, null, returnedState, error, "QQ 音乐登录未完成");
            return true;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            callback = new QqAuthorizationCallback(false, null, returnedState, null, "QQ 音乐登录回调缺少授权结果");
            return false;
        }

        callback = new QqAuthorizationCallback(true, code, returnedState, null, "QQ 音乐登录回调已接收");
        return true;
    }

    private static Dictionary<string, string> ParseParameters(string query, string fragment)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddParameters(values, query);
        AddParameters(values, fragment);
        return values;
    }

    private static void AddParameters(Dictionary<string, string> values, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var text = raw[0] is '?' or '#' ? raw[1..] : raw;
        foreach (var segment in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator >= segment.Length - 1) continue;
            var key = Decode(segment[..separator]);
            var value = Decode(segment[(separator + 1)..]);
            if (key.Length == 0 || value.Length == 0 || key.Length > 128 || value.Length > MaxComponentLength)
                continue;
            values.TryAdd(key, value);
        }
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }
}

public static class QqAuthorizationCookieExtractor
{
    private const int MaxCookieValueLength = 16 * 1024;
    private static readonly string[] CookieOrder =
    [
        "uin",
        "wxuin",
        "qm_keyst",
        "qqmusic_key",
        "p_skey",
        "skey"
    ];
    private static readonly IReadOnlyCollection<string> ReadOnlyCookieNames = Array.AsReadOnly(CookieOrder);

    public static IReadOnlyCollection<string> AllowedCookieNames => ReadOnlyCookieNames;

    /// <summary>
    /// Builds the single session credential consumed by the QQ playback and
    /// account boundaries. Only QQ identity/key cookies are retained; all
    /// tracking, UI and unrelated cookies are discarded in memory.
    /// </summary>
    public static bool TryExtractSession(
        IReadOnlyDictionary<string, string>? cookies,
        out string session)
    {
        session = string.Empty;
        if (cookies is null || cookies.Count == 0) return false;

        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CookieOrder)
        {
            if (!TryGetCookie(cookies, name, out var value) || !IsSafeCookieValue(value)) continue;
            filtered[name] = value.Trim();
        }

        var identity = First(filtered, "uin", "wxuin");
        var key = First(filtered, "qm_keyst", "qqmusic_key", "p_skey", "skey");
        if (!IsNumericQqIdentity(identity) || string.IsNullOrWhiteSpace(key)) return false;

        session = string.Join("; ", CookieOrder
            .Where(filtered.ContainsKey)
            .Select(name => $"{name}={filtered[name]}"));
        return session.Length > 0;
    }

    private static bool IsSafeCookieValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxCookieValueLength &&
        value.All(character => !char.IsControl(character) && character is not ';');

    private static bool IsNumericQqIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var numeric = value.Trim().TrimStart('o', 'O');
        return long.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    }

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] names) =>
        names.Select(name => values.TryGetValue(name, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool TryGetCookie(
        IReadOnlyDictionary<string, string> cookies,
        string name,
        out string value)
    {
        if (cookies.TryGetValue(name, out value!)) return true;
        foreach (var pair in cookies)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>
/// Concrete, provider-isolated QQ flow. It is usable with a future WebView2
/// host through <see cref="IQqAuthorizationBrowser"/> and does not require a
/// WebView2 package at compile time.
/// </summary>
public sealed class QqAuthorizationFlow : IQqAuthorizationFlow
{
    private static readonly Uri LoginUri = new("https://y.qq.com/");
    private readonly IQqAuthorizationBrowser _browser;
    private readonly IQqAuthorizationProbe _probe;

    public QqAuthorizationFlow(IQqAuthorizationBrowser browser, IQqAuthorizationProbe probe)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = CreateState();
        QqAuthorizationBrowserCapture? capture;
        try
        {
            capture = await _browser.CaptureAsync(
                new QqAuthorizationBrowserRequest(LoginUri, state, QqProviderDomainPolicy.AllowedHosts,
                    QqAuthorizationCookieExtractor.AllowedCookieNames), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return NotAuthorized("QQ 音乐登录窗口未完成"); }

        if (capture is null || capture.IsCancelled)
            return NotAuthorized("QQ 音乐登录已取消");
        if (!QqProviderDomainPolicy.IsAllowedHttps(capture.CompletedUri))
            return NotAuthorized("QQ 音乐登录页面不受信任");
        // QQ's first-party web login may finish on a cookie-bearing y.qq.com
        // page rather than an OAuth callback. Validate a callback when it is
        // actually present, then require the live account probe in all cases.
        if (HasAuthorizationCallback(capture.CompletedUri) &&
            (!QqAuthorizationCallbackExtractor.TryExtract(capture.CompletedUri, state, out var callback) || !callback.IsValid))
            return NotAuthorized(callback.SafeMessage);
        if (!QqAuthorizationCookieExtractor.TryExtractSession(capture.Cookies, out var session))
            return NotAuthorized("QQ 音乐登录未返回有效会话");

        var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [QqAuthCredentialNames.Session] = session
        };
        var stateResult = await CheckAsync(credentials, cancellationToken);
        if (stateResult is not (CredentialState.Valid or CredentialState.Expiring))
            return new PlatformAuthorizationSession(
                PlatformId.QqMusic,
                stateResult,
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                stateResult == CredentialState.Expired ? "QQ 音乐登录已过期" : "QQ 音乐登录状态无法确认");

        return new PlatformAuthorizationSession(
            PlatformId.QqMusic,
            stateResult,
            credentials,
            null,
            stateResult == CredentialState.Expiring ? "QQ 音乐登录即将过期" : "QQ 音乐授权成功");
    }

    public async Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials is null || !credentials.TryGetValue(QqAuthCredentialNames.Session, out var session) ||
            !QqAuthorizationCookieExtractor.TryExtractSession(
                ParseSession(session), out _))
            return CredentialState.NotAuthorized;

        try
        {
            var safe = new Dictionary<string, string>(StringComparer.Ordinal) { [QqAuthCredentialNames.Session] = session.Trim() };
            var state = await _probe.CheckAsync(safe, cancellationToken);
            return state is CredentialState.Valid or CredentialState.Expiring or CredentialState.Expired
                ? state
                : CredentialState.Error;
        }
        catch (OperationCanceledException) { throw; }
        catch { return CredentialState.Error; }
    }

    private static IReadOnlyDictionary<string, string> ParseSession(string? session)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(session)) return result;
        foreach (var segment in session.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator >= segment.Length - 1) continue;
            result[segment[..separator].Trim()] = segment[(separator + 1)..].Trim();
        }
        return result;
    }

    private static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool HasAuthorizationCallback(Uri? uri)
    {
        if (uri is null) return false;
        var components = $"{uri.Query}&{uri.Fragment}";
        return components.Contains("state=", StringComparison.OrdinalIgnoreCase) ||
               components.Contains("code=", StringComparison.OrdinalIgnoreCase) ||
               components.Contains("error=", StringComparison.OrdinalIgnoreCase);
    }

    private static PlatformAuthorizationSession NotAuthorized(string message) => new(
        PlatformId.QqMusic,
        CredentialState.NotAuthorized,
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        string.IsNullOrWhiteSpace(message) ? "QQ 音乐登录未完成" : message);
}
