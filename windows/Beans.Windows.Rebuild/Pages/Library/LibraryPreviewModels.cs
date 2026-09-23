using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Pages.Shared;

namespace Beans.Windows.Rebuild.Pages.Library;

/// <summary>
/// Page-local preview data. It deliberately does not represent persisted user data.
/// </summary>
public sealed record LibraryPlaylistPreview(
    string Id,
    string Title,
    string Description,
    string CoverUri,
    PlatformId Platform,
    int TrackCount,
    bool IsPlayable);

public sealed record LibraryTrackPreview(
    string Id,
    string Title,
    string Artist,
    string Album,
    string Duration,
    string CoverUri,
    PlatformId Platform,
    bool IsPlayable,
    string PlaybackBoundary,
    SearchDataOrigin DataOrigin = SearchDataOrigin.Preview,
    string Quality = "未知",
    string? ProviderMediaId = null)
{
    public string PlatformLabel => Platform.ToDisplayName();
    public bool CanAttemptPlayback => TryCreateSearchResult(out _);

    public bool TryCreateSearchResult(out SearchResultItem? item) => OnlinePageTrackMapper.TryCreateSearchResult(
        Platform, Id, Title, Artist, Album, OnlinePageTrackMapper.ParseDuration(Duration), CoverUri, Quality, DataOrigin, out item, ProviderMediaId);
}

public static class LibraryPreviewData
{
    public static IReadOnlyList<LibraryPlaylistPreview> Playlists { get; } =
    [
        new("favorites", "收藏的歌曲", "你收藏的好音乐", "ms-appx:///Assets/Home/track-sunlight.jpg", PlatformId.Beans, 126, false),
        new("focus", "学习专注", "适合专注工作的节奏", "ms-appx:///Assets/Home/playlist-coffee.jpg", PlatformId.Beans, 48, false),
        new("summer", "夏日微风 · 温柔旋律", "QQ 音乐公开歌单预览", "ms-appx:///Assets/Home/playlist-sea.jpg", PlatformId.QqMusic, 30, false),
        new("cloud", "午后咖啡时光", "网易云音乐歌单预览", "ms-appx:///Assets/Home/playlist-sunset.jpg", PlatformId.NetEaseMusic, 24, false)
    ];

    public static IReadOnlyList<LibraryTrackPreview> RecentTracks { get; } =
    [
        new("local-sunny", "晴天", "周杰伦", "叶惠美", "04:29", "ms-appx:///Assets/Home/hero-mountain-lake.jpg", PlatformId.Local, false, OnlinePageTrackMapper.PreviewUnavailableMessage),
        new("qq-north", "一路向北", "周杰伦", "JAY", "04:55", "ms-appx:///Assets/Home/track-sunlight.jpg", PlatformId.QqMusic, false, OnlinePageTrackMapper.PreviewUnavailableMessage),
        new("netease-letting-go", "Letting Go", "蔡健雅", "说到爱", "04:42", "ms-appx:///Assets/Home/playlist-sea.jpg", PlatformId.NetEaseMusic, false, OnlinePageTrackMapper.PreviewUnavailableMessage)
    ];

    public static PlaylistPreviewDetails GetPlaylist(string? id)
    {
        var playlist = Playlists.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Playlists[0];
        var tracks = playlist.Id switch
        {
            "favorites" => RecentTracks,
            "focus" => RecentTracks.Take(2).ToArray(),
            "summer" => RecentTracks.Where(track => track.Platform == PlatformId.QqMusic).ToArray(),
            "cloud" => RecentTracks.Where(track => track.Platform == PlatformId.NetEaseMusic).ToArray(),
            _ => Array.Empty<LibraryTrackPreview>()
        };
        return new PlaylistPreviewDetails(playlist, "这是 Beans 音乐中的预览歌单。真实平台歌单将在对应数据适配完成后加载。", tracks);
    }
}

public sealed record PlaylistPreviewDetails(
    LibraryPlaylistPreview Playlist,
    string Description,
    IReadOnlyList<LibraryTrackPreview> Tracks)
{
    public bool CanAttemptPlayback => Tracks.Any(track => track.CanAttemptPlayback);
}
