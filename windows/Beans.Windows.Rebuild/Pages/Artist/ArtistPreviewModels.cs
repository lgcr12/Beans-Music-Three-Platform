using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Pages.Shared;

namespace Beans.Windows.Rebuild.Pages.Artist;

public sealed record DetailTrackPreview(
    string NativeId,
    string Title,
    string Artist,
    string Album,
    string Duration,
    PlatformId Platform,
    bool IsPlayable,
    string RestrictionState,
    SearchDataOrigin DataOrigin = SearchDataOrigin.Preview,
    string CoverUri = "",
    string Quality = "未知")
{
    public bool CanAttemptPlayback => TryCreateSearchResult(out _);

    public bool TryCreateSearchResult(out SearchResultItem? item) => OnlinePageTrackMapper.TryCreateSearchResult(
        Platform, NativeId, Title, Artist, Album, OnlinePageTrackMapper.ParseDuration(Duration), CoverUri, Quality, DataOrigin, out item);
}

public sealed record DetailAlbumPreview(string NativeId, string Title, string Year, string CoverUri, PlatformId Platform);

public sealed record ArtistPreviewContent(
    string NativeId,
    string Name,
    string Description,
    string ImageUri,
    PlatformId Platform,
    IReadOnlyList<DetailTrackPreview> Tracks,
    IReadOnlyList<DetailAlbumPreview> Albums);

public static class DetailPreviewData
{
    public static ArtistPreviewContent Artist(PlatformRouteParameter? route)
    {
        var platform = Parse(route?.PlatformId);
        var name = string.IsNullOrWhiteSpace(route?.Title) ? "周杰伦" : route.Title;
        return new(route?.NativeId ?? "preview-artist", name, "歌手资料为明确标记的页面预览，真实详情将在平台详情适配完成后加载。",
            "ms-appx:///Assets/Home/hero-mountain-lake.jpg", platform,
            Tracks(platform, name),
            [
                new("preview-album-1", "叶惠美", "2003", "ms-appx:///Assets/Home/playlist-sunset.jpg", platform),
                new("preview-album-2", "十一月的萧邦", "2005", "ms-appx:///Assets/Home/playlist-forest.jpg", platform)
            ]);
    }

    public static IReadOnlyList<DetailTrackPreview> Tracks(PlatformId platform, string artist) =>
    [
        new("preview-track-1", "晴天", artist, "叶惠美", "04:29", platform, false,
            OnlinePageTrackMapper.PreviewUnavailableMessage),
        new("preview-track-2", "一路向北", artist, "JAY", "04:55", platform, false,
            OnlinePageTrackMapper.PreviewUnavailableMessage)
    ];

    public static PlatformId Parse(string? value) => PlatformIdExtensions.TryParseStableId(value ?? string.Empty, out var platform)
        ? platform
        : PlatformId.Beans;
}
