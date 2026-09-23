using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

public static class QqDiscoveryRequestKeys
{
    public const string Hero = "qq:discovery:hero:anonymous";
    public const string Rankings = "qq:discovery:rankings:anonymous";
    public const string RecommendedPlaylists = "qq:discovery:recommended-playlists:anonymous";
    public const string Categories = "qq:discovery:categories:anonymous";

    public static string PlaylistSquare(string? category, int page)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "all" : category.Trim().Replace(':', '-');
        var key = $"qq:discovery:playlist-square:{normalizedCategory}:page:{page}:anonymous";
        RequestKeyValidator.Validate("qq", key);
        return key;
    }
}
