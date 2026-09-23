using System.Security.Cryptography;
using System.Text;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

public static class QqSearchRequestKeys
{
    public static string Search(SearchResultFilter filter, string keyword, int page, int pageSize) =>
        $"qq:search:{FilterName(filter)}:{SafeKeywordSegment(keyword)}:{Math.Max(page, 1)}:{Math.Clamp(pageSize, 1, 50)}";

    public static string Suggestions(string keyword) =>
        $"qq:search:suggestions:{SafeKeywordSegment(keyword)}:1:6";

    public static DiscoveryCacheKey Storage(string requestKey) =>
        new("qq", "search", requestKey, 1, AccountIdentityHasher.Anonymous);

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
                          new[] { "cookie", "token", "authorization", "password", "secret", "session", "csrf" }
                              .Any(value => normalized.Contains(value, StringComparison.OrdinalIgnoreCase));
        if (!unsafeValue) return normalized;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha256-" + Convert.ToHexString(digest[..12]).ToLowerInvariant();
    }
}
