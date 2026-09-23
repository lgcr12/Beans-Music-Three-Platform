using System.Globalization;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms.NetEase.Dto.Search;

namespace Beans.Windows.Rebuild.Services.Platforms.NetEase;

internal static class NetEaseSearchMapper
{
    private const string PlaceholderImage = "ms-appx:///Assets/Branding/beans-icon.png";
    private const string PlaybackRestriction = "播放时需要登录并解析网易云音乐官方播放地址";

    public static IReadOnlyList<SearchResultItem> Tracks(NetEaseTrackSearchPayload payload) => payload.Result.Songs!
        .Select(item =>
        {
            var nativeId = item.Id!.Value.ToString(CultureInfo.InvariantCulture);
            var artists = item.Artists ?? item.AlternateArtists ?? [];
            var artist = JoinArtists(artists);
            var albumDto = item.Album ?? item.AlternateAlbum;
            var album = FirstNonEmpty(albumDto?.Name, "未知专辑");
            var durationMs = item.DurationMilliseconds ?? item.AlternateDurationMilliseconds;
            return new SearchResultItem(
                SearchResultType.Track, PlatformId.NetEaseMusic, nativeId, $"netease:track:{nativeId}", item.Name!.Trim(),
                Subtitle: $"{artist} · {album}", Artist: artist, Album: album,
                CoverUri: NormalizeImage(FirstNonEmpty(albumDto?.PictureUrl, albumDto?.BlurPictureUrl)),
                Duration: durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
                Quality: Quality(item), IsPlayable: false, RestrictionState: PlaybackRestriction,
                SourceDisplayName: "网易云音乐", SourceBadgeText: "网易云音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"netease:track:{nativeId}");
        })
        .ToArray();

    public static IReadOnlyList<SearchResultItem> Albums(NetEaseAlbumSearchPayload payload) => payload.Result.Albums!
        .Select(item =>
        {
            var nativeId = item.Id!.Value.ToString(CultureInfo.InvariantCulture);
            var artist = FirstNonEmpty(item.Artist?.Name, JoinArtists(item.Artists ?? []), "未知歌手");
            var releaseDate = FormatDate(item.PublishTime);
            return new SearchResultItem(
                SearchResultType.Album, PlatformId.NetEaseMusic, nativeId, $"netease:album:{nativeId}", item.Name!.Trim(),
                Subtitle: string.IsNullOrWhiteSpace(releaseDate) ? artist : $"{artist} · {releaseDate}", Artist: artist,
                CoverUri: NormalizeImage(item.PictureUrl), IsPlayable: false, RestrictionState: PlaybackRestriction,
                SourceDisplayName: "网易云音乐", SourceBadgeText: "网易云音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"netease:album:{nativeId}");
        })
        .ToArray();

    public static IReadOnlyList<SearchResultItem> Artists(NetEaseArtistSearchPayload payload) => payload.Result.Artists!
        .Select(item =>
        {
            var nativeId = item.Id!.Value.ToString(CultureInfo.InvariantCulture);
            var title = item.Name!.Trim();
            var subtitle = FirstNonEmpty(item.BriefDescription, item.Aliases?.FirstOrDefault(), "网易云音乐歌手");
            return new SearchResultItem(
                SearchResultType.Artist, PlatformId.NetEaseMusic, nativeId, $"netease:artist:{nativeId}", title,
                Subtitle: subtitle, Artist: title, CoverUri: NormalizeImage(FirstNonEmpty(item.PictureUrl, item.AvatarUrl)),
                IsPlayable: false, RestrictionState: PlaybackRestriction, SourceDisplayName: "网易云音乐",
                SourceBadgeText: "网易云音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"netease:artist:{nativeId}");
        })
        .ToArray();

    private static string Quality(NetEaseSearchTrackDto item)
    {
        if (item.Lossless is not null) return "SQ";
        if (item.High?.Bitrate is > 0) return $"{item.High.Bitrate.Value / 1000}K";
        if (item.Medium?.Bitrate is > 0) return $"{item.Medium.Bitrate.Value / 1000}K";
        if (item.Low?.Bitrate is > 0) return $"{item.Low.Bitrate.Value / 1000}K";
        return "未知";
    }

    private static string JoinArtists(IReadOnlyList<NetEaseSearchArtistRefDto> artists)
    {
        var value = string.Join(" / ", artists.Select(item => item.Name?.Trim()).Where(name => !string.IsNullOrWhiteSpace(name)));
        return string.IsNullOrWhiteSpace(value) ? "未知歌手" : value;
    }

    private static string FormatDate(long? timestamp)
    {
        if (timestamp is not > 0) return string.Empty;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return string.Empty; }
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
