using System.Security.Cryptography;
using System.Text;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Accounts;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

/// <summary>
/// Small browser boundary for the provider-owned NetEase login window. The
/// application does not depend on WebView2 types here; a host supplies an
/// isolated browser profile and returns only the filtered capture.
/// </summary>
public interface INetEaseAuthorizationBrowser
{
    Task<NetEaseAuthorizationBrowserCapture> CaptureAsync(
        NetEaseAuthorizationBrowserRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider account probe. Browser cookies are not considered authorized
/// until NetEase accepts them through this provider-owned probe.
/// </summary>
public interface INetEaseAuthorizationProbe
{
    Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken);
}

public sealed record NetEaseAuthorizationBrowserRequest(
    Uri LoginUri,
    string State,
    IReadOnlyCollection<string> AllowedHosts,
    IReadOnlyCollection<string> AllowedCookieNames);

public sealed record NetEaseAuthorizationBrowserCapture(
    Uri? CompletedUri,
    IReadOnlyDictionary<string, string>? Cookies,
    bool IsCancelled = false);

/// <summary>
/// Exact provider host policy. A suffix check is intentionally avoided so a
/// look-alike such as music.163.com.attacker.example cannot receive cookies.
/// </summary>
public static class NetEaseProviderDomainPolicy
{
    private static readonly string[] Hosts = ["music.163.com"];

    public static IReadOnlyCollection<string> AllowedHosts => Hosts;

    public static bool IsAllowedHttps(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || (uri.Port is not (-1 or 443)))
            return false;

        return Hosts.Any(host => host.Equals(uri.DnsSafeHost, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Validates an optional provider callback state. NetEase commonly completes
/// QR/web login by leaving a cookie-bearing page without an OAuth callback, so
/// a missing state is permitted; when a state is present it must match the
/// flow nonce exactly.
/// </summary>
public static class NetEaseAuthorizationCallbackValidator
{
    public static bool IsValid(Uri? completionUri, string expectedState)
    {
        if (!NetEaseProviderDomainPolicy.IsAllowedHttps(completionUri) ||
            string.IsNullOrWhiteSpace(expectedState))
            return false;

        var returnedState = ParseParameters(completionUri!.Query, completionUri.Fragment)
            .FirstOrDefault(pair => pair.Key.Equals("state", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(returnedState))
            return true;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedState),
            Encoding.UTF8.GetBytes(returnedState));
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
        if (string.IsNullOrWhiteSpace(raw))
            return;
        var text = raw[0] is '?' or '#' ? raw[1..] : raw;
        foreach (var segment in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator >= segment.Length - 1)
                continue;
            var key = Decode(segment[..separator]);
            var value = Decode(segment[(separator + 1)..]);
            if (key.Length > 0 && value.Length > 0 && key.Length <= 128 && value.Length <= 4096)
                values.TryAdd(key, value);
        }
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return string.Empty; }
    }
}

/// <summary>
/// Filters browser cookies down to the small set needed for a NetEase
/// account/session request. Unrelated tracking and UI cookies never leave the
/// provider flow boundary.
/// </summary>
public static class NetEaseAuthorizationCookieExtractor
{
    private const int MaxCookieValueLength = 16 * 1024;
    private static readonly string[] CookieOrder =
    [
        "MUSIC_U",
        "MUSIC_A",
        "__csrf",
        "NMTID",
        "ntes_utid"
    ];

    public static IReadOnlyCollection<string> AllowedCookieNames => CookieOrder;

    public static bool TryExtractSession(
        IReadOnlyDictionary<string, string>? cookies,
        out string session)
    {
        session = string.Empty;
        if (cookies is null || cookies.Count == 0)
            return false;

        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CookieOrder)
        {
            if (!cookies.TryGetValue(name, out var value) || !IsSafeCookieValue(value))
                continue;
            filtered[name] = value.Trim();
        }

        // MUSIC_U is the account session marker. MUSIC_A alone can be an
        // anonymous/temporary value and must not be treated as a login.
        if (!filtered.ContainsKey("MUSIC_U"))
            return false;

        session = string.Join("; ", CookieOrder
            .Where(filtered.ContainsKey)
            .Select(name => $"{name}={filtered[name]}"));
        return session.Length > 0;
    }

    private static bool IsSafeCookieValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxCookieValueLength &&
        value.All(character => !char.IsControl(character) && character is not ';');
}

/// <summary>
/// Concrete provider flow that can be connected to a real isolated WebView2
/// host later. It is deliberately independent of WebView2 at compile time and
/// does not write credentials; the shared auth coordinator owns persistence.
/// </summary>
public sealed class NetEaseAuthorizationFlow : INetEaseAuthorizationFlow
{
    private static readonly Uri LoginUri = new("https://music.163.com/");
    private readonly INetEaseAuthorizationBrowser _browser;
    private readonly INetEaseAuthorizationProbe _probe;

    public NetEaseAuthorizationFlow(
        INetEaseAuthorizationBrowser browser,
        INetEaseAuthorizationProbe probe)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<PlatformAuthorizationSession> AuthorizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = CreateState();
        NetEaseAuthorizationBrowserCapture? capture;
        try
        {
            capture = await _browser.CaptureAsync(
                new NetEaseAuthorizationBrowserRequest(
                    LoginUri,
                    state,
                    NetEaseProviderDomainPolicy.AllowedHosts,
                    NetEaseAuthorizationCookieExtractor.AllowedCookieNames),
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return NotAuthorized("网易云音乐登录窗口未完成"); }

        if (capture is null || capture.IsCancelled)
            return NotAuthorized("网易云音乐登录已取消");
        if (!NetEaseAuthorizationCallbackValidator.IsValid(capture.CompletedUri, state))
            return NotAuthorized("网易云音乐登录页面不受信任");
        if (!NetEaseAuthorizationCookieExtractor.TryExtractSession(capture.Cookies, out var session))
            return NotAuthorized("网易云音乐登录未返回有效会话");

        var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NetEaseAuthCredentialNames.Session] = session
        };
        var stateResult = await CheckAsync(credentials, cancellationToken);
        if (stateResult is not (CredentialState.Valid or CredentialState.Expiring))
            return new PlatformAuthorizationSession(
                PlatformId.NetEaseMusic,
                stateResult,
                new Dictionary<string, string>(StringComparer.Ordinal),
                null,
                stateResult == CredentialState.Expired
                    ? "网易云音乐登录已过期"
                    : "网易云音乐登录状态无法确认");

        return new PlatformAuthorizationSession(
            PlatformId.NetEaseMusic,
            stateResult,
            credentials,
            null,
            stateResult == CredentialState.Expiring
                ? "网易云音乐登录即将过期"
                : "网易云音乐授权成功");
    }

    public async Task<CredentialState> CheckAsync(
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials is null ||
            !credentials.TryGetValue(NetEaseAuthCredentialNames.Session, out var session) ||
            !NetEaseAuthorizationCookieExtractor.TryExtractSession(ParseSession(session), out _))
            return CredentialState.NotAuthorized;

        try
        {
            var safe = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NetEaseAuthCredentialNames.Session] = session.Trim()
            };
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
        if (string.IsNullOrWhiteSpace(session))
            return result;

        foreach (var segment in session.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator >= segment.Length - 1)
                continue;
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

    private static PlatformAuthorizationSession NotAuthorized(string message) => new(
        PlatformId.NetEaseMusic,
        CredentialState.NotAuthorized,
        new Dictionary<string, string>(StringComparer.Ordinal),
        null,
        string.IsNullOrWhiteSpace(message) ? "网易云音乐登录未完成" : message);
}
