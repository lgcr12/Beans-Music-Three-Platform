using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms.QQ.Dto.Search;

namespace Beans.Windows.Rebuild.Services.Platforms.QQ;

internal static class QqSearchMapper
{
    private const string PlaceholderImage = "ms-appx:///Assets/Branding/beans-icon.png";
    private const string PlaybackRestriction = "播放时需要登录并解析 QQ 音乐官方播放地址";

    public static IReadOnlyList<SearchResultItem> Tracks(QqTrackSearchPayload payload) => payload.Page.Items!
        .Select(item =>
        {
            var nativeId = item.SongMid!.Trim();
            var title = FirstNonEmpty(item.SongName, item.Name);
            var artist = JoinSingers(item.Singers);
            var album = FirstNonEmpty(item.AlbumName, "未知专辑");
            var albumMid = FirstNonEmpty(item.AlbumMid);
            return new SearchResultItem(
                SearchResultType.Track, PlatformId.QqMusic, nativeId, $"qq:track:{nativeId}", title,
                Subtitle: $"{artist} · {album}", Artist: artist, Album: album,
                CoverUri: AlbumCover(albumMid),
                Duration: item.DurationSeconds is > 0 ? TimeSpan.FromSeconds(item.DurationSeconds.Value) : null,
                Quality: Quality(item.File), IsPlayable: false, RestrictionState: PlaybackRestriction,
                SourceDisplayName: "QQ 音乐", SourceBadgeText: "QQ 音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"qq:track:{nativeId}",
                ProviderMediaId: FirstNonEmpty(item.File?.MediaMid, nativeId));
        })
        .ToArray();

    public static IReadOnlyList<SearchResultItem> Albums(QqAlbumSearchPayload payload) => payload.Page.Items!
        .Select(item =>
        {
            var nativeId = FirstNonEmpty(item.AlbumMid, item.Mid);
            var title = FirstNonEmpty(item.AlbumName, item.Name);
            var artist = JoinSingers(item.Singers, item.SingerName);
            var year = FirstNonEmpty(item.PublicTime);
            return new SearchResultItem(
                SearchResultType.Album, PlatformId.QqMusic, nativeId, $"qq:album:{nativeId}", title,
                Subtitle: string.IsNullOrWhiteSpace(year) ? artist : $"{artist} · {year}", Artist: artist,
                CoverUri: AlbumCover(nativeId), IsPlayable: false, RestrictionState: PlaybackRestriction,
                SourceDisplayName: "QQ 音乐", SourceBadgeText: "QQ 音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"qq:album:{nativeId}");
        })
        .ToArray();

    public static IReadOnlyList<SearchResultItem> Artists(QqArtistSearchPayload payload) => payload.Group.Items!
        .Select(item =>
        {
            var nativeId = item.Mid!.Trim();
            var title = item.Name!.Trim();
            return new SearchResultItem(
                SearchResultType.Artist, PlatformId.QqMusic, nativeId, $"qq:artist:{nativeId}", title,
                Subtitle: "QQ 音乐歌手", Artist: title, CoverUri: ArtistCover(nativeId, item.Picture),
                IsPlayable: false, RestrictionState: PlaybackRestriction, SourceDisplayName: "QQ 音乐",
                SourceBadgeText: "QQ 音乐", DataOrigin: SearchDataOrigin.Live,
                PayloadReference: $"qq:artist:{nativeId}");
        })
        .ToArray();

    public static IReadOnlyList<SearchSuggestion> Suggestions(QqSuggestionPayload payload) =>
        Groups(payload.Data)
            .SelectMany(group => group.Items ?? [])
            .Select(item => new SearchSuggestion(item.Name!.Trim(), FirstNonEmpty(item.Singer, "QQ 音乐"), PlatformId.QqMusic))
            .GroupBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(6)
            .ToArray();

    private static IEnumerable<QqSmartboxGroupDto> Groups(QqSmartboxDataDto data)
    {
        if (data.Singers is not null) yield return data.Singers;
        if (data.Songs is not null) yield return data.Songs;
        if (data.Albums is not null) yield return data.Albums;
    }

    private static string JoinSingers(IReadOnlyList<QqSingerDto>? singers, string? fallback = null)
    {
        var value = string.Join(" / ", (singers ?? []).Select(item => item.Name?.Trim()).Where(name => !string.IsNullOrWhiteSpace(name)));
        return string.IsNullOrWhiteSpace(value) ? FirstNonEmpty(fallback, "未知歌手") : value;
    }

    private static string Quality(QqTrackFileDto? file)
    {
        if (file?.SizeFlac > 0) return "FLAC";
        if (file?.SizeApe > 0) return "APE";
        if (file?.Size320 > 0) return "320K";
        if (file?.Size128 > 0) return "128K";
        return "未知";
    }

    private static string AlbumCover(string? mid) => string.IsNullOrWhiteSpace(mid)
        ? PlaceholderImage
        : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{mid.Trim()}.jpg";

    private static string ArtistCover(string mid, string? raw)
    {
        var normalized = NormalizeImage(raw);
        return normalized ?? $"https://y.gtimg.cn/music/photo_new/T001R300x300M000{mid}.jpg";
    }

    private static string? NormalizeImage(string? raw)
    {
        var value = raw?.Trim().Replace("\\/", "/");
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        else if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        else if (value.StartsWith("/", StringComparison.Ordinal)) value = "https://y.gtimg.cn" + value;
        else if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "https://y.gtimg.cn/" + value.TrimStart('/');
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
