using System.Globalization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms.QQ.Dto;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

internal static class QqDiscoveryMapper
{
    private const string PlaceholderImage = "ms-appx:///Assets/Branding/beans-icon.png";

    public static IReadOnlyList<RankingSummary> Rankings(QqRankingsPayload payload) => payload.Items
        .Select(item => new RankingSummary(
            item.Id!.Value.ToString(CultureInfo.InvariantCulture),
            item.TopTitle?.Trim() ?? item.Title!.Trim(),
            FirstNonEmpty(item.UpdateTips, item.SubTitle, "QQ 音乐公开榜单"),
            NormalizeImage(item.PicUrl),
            (item.SongList ?? [])
                .Select(song => song.SongName?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(3)
                .Select(name => name!)
                .ToArray(),
            "qq",
            "QQ 音乐"))
        .ToArray();

    public static IReadOnlyList<MusicPlaylist> Playlists(QqPlaylistsPayload payload) => payload.Items
        .Select(item =>
        {
            var nativeId = (item.Tid ?? item.Id)!.Value.ToString(CultureInfo.InvariantCulture);
            var creator = FirstNonEmpty(item.Creator?.Nick, item.Creator?.Name, item.UserName, "QQ 音乐");
            var cover = new Uri(NormalizeImage(FirstNonEmpty(item.Cover, item.PictureUrl)));
            var safeReference = $"qq:playlist:{nativeId}";
            return new MusicPlaylist(
                new MusicIdentity(PlatformId.QqMusic, nativeId),
                item.Title!.Trim(),
                creator,
                cover,
                item.SongCount,
                safeReference,
                item.PlayCount,
                null,
                false,
                "QQ 音乐",
                safeReference);
        })
        .ToArray();

    public static DiscoveryHero Hero(IReadOnlyList<MusicPlaylist> playlists)
    {
        var playlist = playlists.First();
        return new DiscoveryHero(
            playlist.Title,
            "QQ 音乐公开推荐歌单",
            string.IsNullOrWhiteSpace(playlist.CoverUri) ? PlaceholderImage : playlist.CoverUri,
            "查看歌单",
            playlist.Identity.NativeId);
    }

    public static DiscoveryHero Hero(IReadOnlyList<RankingSummary> rankings)
    {
        var ranking = rankings.First();
        return new DiscoveryHero(
            ranking.Title,
            "QQ 音乐公开排行榜",
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
        else if (value.StartsWith("/", StringComparison.Ordinal)) value = "https://y.gtimg.cn" + value;
        else if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                 !value.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
            value = "https://y.gtimg.cn/" + value.TrimStart('/');

        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : PlaceholderImage;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
