using System.Globalization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

internal static class NetEaseDiscoveryMapper
{
    private const string PlaceholderImage = "ms-appx:///Assets/Branding/beans-icon.png";

    public static IReadOnlyList<RankingSummary> Rankings(NetEaseRankingsPayload payload) => payload.Items
        .Select(item => new RankingSummary(
            item.Id!.Value.ToString(CultureInfo.InvariantCulture),
            item.Name!.Trim(),
            FirstNonEmpty(item.UpdateFrequency, "网易云音乐公开榜单"),
            NormalizeImage(item.CoverImgUrl),
            (item.Tracks ?? [])
                .Where(track => !string.IsNullOrWhiteSpace(track.Name))
                .Take(3)
                .Select(track => track.Name!.Trim())
                .ToArray(),
            "netease",
            "网易云音乐"))
        .ToArray();

    public static IReadOnlyList<MusicPlaylist> Playlists(NetEasePlaylistsPayload payload) => payload.Items
        .Select(item =>
        {
            var nativeId = item.Id!.Value.ToString(CultureInfo.InvariantCulture);
            var reference = $"netease:playlist:{nativeId}";
            var category = item.Tags?.FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag))?.Trim();
            return new MusicPlaylist(
                new MusicIdentity(PlatformId.NetEaseMusic, nativeId),
                item.Name!.Trim(),
                FirstNonEmpty(item.Creator?.Nickname, "网易云音乐"),
                new Uri(NormalizeImage(item.CoverImgUrl)),
                item.TrackCount,
                reference,
                item.PlayCount,
                category,
                false,
                "网易云音乐",
                reference);
        })
        .ToArray();

    public static IReadOnlyList<string> Categories(NetEaseCategoriesPayload payload) => payload.Items;

    public static DiscoveryHero Hero(IReadOnlyList<MusicPlaylist> playlists)
    {
        var playlist = playlists.First();
        return new DiscoveryHero(
            playlist.Title,
            "网易云音乐公开推荐歌单",
            string.IsNullOrWhiteSpace(playlist.CoverUri) ? PlaceholderImage : playlist.CoverUri,
            "查看歌单",
            playlist.Identity.NativeId);
    }

    public static DiscoveryHero Hero(IReadOnlyList<RankingSummary> rankings)
    {
        var ranking = rankings.First();
        return new DiscoveryHero(
            ranking.Title,
            "网易云音乐公开排行榜",
            string.IsNullOrWhiteSpace(ranking.ImageUri) ? PlaceholderImage : ranking.ImageUri,
            "查看榜单",
            ranking.Id);
    }

    private static string NormalizeImage(string? raw)
    {
        var value = raw?.Trim().Replace("\\/", "/");
        if (string.IsNullOrWhiteSpace(value)) return PlaceholderImage;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        else if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : PlaceholderImage;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
