using System.Security.Cryptography;
using System.Text;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public static class NetEaseSearchRequestKeys
{
    public static string Search(SearchResultFilter filter, string keyword, int page, int pageSize) =>
        $"netease:search:{FilterName(filter)}:{SafeKeywordSegment(keyword)}:{Math.Max(page, 1)}:{Math.Clamp(pageSize, 1, 50)}";

    public static string Suggestions(string keyword) =>
        $"netease:search:suggestions:{SafeKeywordSegment(keyword)}:1:6";

    public static DiscoveryCacheKey Storage(string requestKey) =>
        new("netease", "search", requestKey, 1, AccountIdentityHasher.Anonymous);

    private static string FilterName(SearchResultFilter filter) => filter switch
    {
        SearchResultFilter.All => "all",
        SearchResultFilter.Tracks => "tracks",
        SearchResultFilter.Albums => "albums",
        SearchResultFilter.Artists => "artists",
        SearchResultFilter.Playlists => "playlists",
        _ => "unknown"
    };

    private static string SafeKeywordSegment(string keyword)
    {
        var normalized = keyword.Trim().ToLowerInvariant();
        var unsafeValue = normalized.Contains(':') || normalized.Contains('?') || normalized.Contains('&') ||
                          new[] { "cookie", "token", "authorization", "password", "secret", "session", "csrf", "signature", "sign=", "api-key", "api_key", "apikey", "key=", "uin=" }
                              .Any(value => normalized.Contains(value, StringComparison.OrdinalIgnoreCase));
        if (!unsafeValue) return normalized;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha256-" + Convert.ToHexString(digest[..12]).ToLowerInvariant();
    }
}

public static class NetEaseSearchProtocol
{
    public static IReadOnlyDictionary<string, object?> CreatePayload(
        string keyword, int type, int offset, int pageSize) =>
        new Dictionary<string, object?>
        {
            ["s"] = keyword,
            ["type"] = type,
            ["limit"] = pageSize,
            ["offset"] = offset,
            ["total"] = true
        };
}
