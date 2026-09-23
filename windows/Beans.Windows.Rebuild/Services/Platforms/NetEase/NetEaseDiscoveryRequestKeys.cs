using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

public static class NetEaseDiscoveryRequestKeys
{
    public const string Hero = "netease:discovery:hero:anonymous";
    public const string Rankings = "netease:discovery:rankings:anonymous";
    public const string RecommendedPlaylists = "netease:discovery:recommended-playlists:anonymous";
    public const string Categories = "netease:discovery:categories:anonymous";

    public static string PlaylistSquare(string? category, int page)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var normalized = string.IsNullOrWhiteSpace(category) ? "all" : category.Trim().Replace(':', '-');
        var key = $"netease:discovery:playlist-square:{normalized}:page:{page}:anonymous";
        RequestKeyValidator.Validate("netease", key);
        return key;
    }
}
