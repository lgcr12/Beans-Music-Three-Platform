using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;

namespace Beans.Windows.Rebuild.PreviewData;

public static class HomePreviewData
{
    private static readonly string PreviewAudioUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "preview-tone.wav")).AbsoluteUri;

    public static IReadOnlyList<HomePlaylist> Playlists { get; } =
    [
        new("summer", "夏日微风 · 温柔旋律", "清新 / 放松 / 治愈", "ms-appx:///Assets/Home/playlist-sea.jpg", "12.8 万", PlatformId.QqMusic),
        new("coffee", "午后咖啡时光", "轻音乐 / 慢生活", "ms-appx:///Assets/Home/playlist-coffee.jpg", "8.6 万", PlatformId.NetEaseMusic),
        new("sunset", "傍晚的浪漫", "流行 / 浪漫 / 情感", "ms-appx:///Assets/Home/playlist-sunset.jpg", "16.3 万", PlatformId.Local),
        new("forest", "自然之声", "纯音乐 / 环境音 / 放松", "ms-appx:///Assets/Home/playlist-forest.jpg", "9.4 万", PlatformId.Beans)
    ];

    public static IReadOnlyList<HomeTrack> RecentTracks { get; } =
    [
        new("preview-qingtian", 1, "晴天", "周杰伦", "叶惠美", "04:29", "ms-appx:///Assets/Home/hero-mountain-lake.jpg", PlatformId.Local, PreviewTrack("preview-qingtian", "晴天", "周杰伦", "叶惠美", PlatformId.Local), true),
        new("north", 2, "一路向北", "周杰伦", "JAY", "04:55", "ms-appx:///Assets/Home/track-sunlight.jpg", PlatformId.QqMusic, PreviewTrack("north", "一路向北", "周杰伦", "JAY", PlatformId.QqMusic)),
        new("letting-go", 3, "Letting Go", "蔡健雅", "说到爱", "04:42", "ms-appx:///Assets/Home/playlist-sea.jpg", PlatformId.NetEaseMusic, PreviewTrack("letting-go", "Letting Go", "蔡健雅", "说到爱", PlatformId.NetEaseMusic)),
        new("bright-star", 4, "夜空中最亮的星", "逃跑计划", "世界", "04:17", "ms-appx:///Assets/Home/playlist-sunset.jpg", PlatformId.Local, PreviewTrack("bright-star", "夜空中最亮的星", "逃跑计划", "世界", PlatformId.Local)),
        new("rice", 5, "稻香", "周杰伦", "魔杰座", "03:43", "ms-appx:///Assets/Home/playlist-forest.jpg", PlatformId.Beans, PreviewTrack("rice", "稻香", "周杰伦", "魔杰座", PlatformId.Beans))
    ];

    public static IReadOnlyList<HomeRecommendation> Recommendations { get; } =
    [
        new("qilixiang", "七里香", "周杰伦", "ms-appx:///Assets/Home/playlist-forest.jpg", PlatformId.QqMusic, PreviewTrack("qilixiang", "七里香", "周杰伦", "", PlatformId.QqMusic)),
        new("qinghuaci", "青花瓷", "周杰伦", "ms-appx:///Assets/Home/playlist-sea.jpg", PlatformId.NetEaseMusic, PreviewTrack("qinghuaci", "青花瓷", "周杰伦", "", PlatformId.NetEaseMusic)),
        new("simple-love", "简单爱", "周杰伦", "ms-appx:///Assets/Home/playlist-coffee.jpg", PlatformId.NetEaseMusic, PreviewTrack("simple-love", "简单爱", "周杰伦", "", PlatformId.NetEaseMusic)),
        new("balloon", "告白气球", "周杰伦", "ms-appx:///Assets/Home/playlist-sunset.jpg", PlatformId.QqMusic, PreviewTrack("balloon", "告白气球", "周杰伦", "", PlatformId.QqMusic)),
        new("rice", "稻香", "周杰伦", "ms-appx:///Assets/Home/track-sunlight.jpg", PlatformId.Beans, PreviewTrack("rice", "稻香", "周杰伦", "", PlatformId.Beans))
    ];

    public static PlaybackItem CurrentPlaybackItem { get; } = new(
        "preview-qingtian",
        "晴天",
        "周杰伦",
        "叶惠美",
        "ms-appx:///Assets/Home/hero-mountain-lake.jpg",
        PreviewAudioUri,
        TimeSpan.FromSeconds(14),
        "本地音乐 · 预览内容",
        "SQ",
        PlatformId.Local,
        "preview-qingtian",
        SearchDataOrigin.Preview);

    public static PlaybackItem ToPlaybackItem(HomeTrack track) => new(
        track.Id,
        track.Title,
        track.Artist,
        track.Album,
        track.ImageUri,
        PreviewAudioUri,
        TimeSpan.FromSeconds(14),
        PlatformLabel(track.SourcePlatform),
        "SQ",
        track.SourcePlatform,
        track.Id,
        SearchDataOrigin.Preview);

    private static SearchResultItem PreviewTrack(
        string id,
        string title,
        string artist,
        string album,
        PlatformId platform) => new(
            SearchResultType.Track,
            platform,
            id,
            $"{platform.ToStableId()}:track:{id}",
            title,
            Artist: artist,
            Album: album,
            IsPlayable: false,
            RestrictionState: "预览内容不能进入真实播放链路",
            SourceDisplayName: platform.ToDisplayName(),
            SourceBadgeText: platform.ToDisplayName(),
            DataOrigin: SearchDataOrigin.Preview);

    private static string PlatformLabel(PlatformId platform) => platform switch
    {
        PlatformId.QqMusic => "QQ 音乐 · 预览内容",
        PlatformId.NetEaseMusic => "网易云音乐 · 预览内容",
        PlatformId.Beans => "Beans 歌单 · 预览内容",
        PlatformId.Local => "本地音乐 · 预览内容",
        _ => "未知来源 · 预览内容"
    };
}
