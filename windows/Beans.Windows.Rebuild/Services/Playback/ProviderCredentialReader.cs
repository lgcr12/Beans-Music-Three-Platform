using Beans.Windows.Rebuild.Services.Security;

namespace Beans.Windows.Rebuild.Services.Playback;

internal static class ProviderCredentialReader
{
    public static async Task<string?> ReadSessionAsync(
        ISecureCredentialStore store,
        string platform,
        CancellationToken cancellationToken)
    {
        var value = await store.ReadAsync(platform, "session", cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static void ApplyCookieHeader(HttpRequestMessage request, string session)
    {
        if (!string.IsNullOrWhiteSpace(session))
            request.Headers.TryAddWithoutValidation("Cookie", session);
    }

    public static IReadOnlyDictionary<string, string> ParseCookieHeader(string session)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in session.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator == segment.Length - 1) continue;
            var name = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0) result[name] = value;
        }
        return result;
    }
}
