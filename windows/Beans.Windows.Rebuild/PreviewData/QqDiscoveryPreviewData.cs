using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.PreviewData;

public static class QqDiscoveryPreviewData
{
    public static PlatformDiscoveryContent Create(string? category = null) => new(
        "qq",
        new DiscoveryHero("QQ 音乐公开精选", "当前展示独立 PreviewData；个性推荐需完成平台授权后接入。", "ms-appx:///Assets/Home/hero-mountain-lake.jpg", "浏览公开内容"),
        [],
        [
            new("qq-top-preview", "巅峰榜 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-sunset.jpg", ["流行脉搏", "城市夜航", "风的方向"], "qq"),
            new("qq-new-preview", "华语新歌 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-sea.jpg", ["沿海公路", "凌晨车站", "慢慢靠近"], "qq"),
            new("qq-global-preview", "欧美精选 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-coffee.jpg", ["Paper Moon", "Ocean Lights", "Drive Home"], "qq")
        ],
        [
            DiscoveryPreviewDataFactory.Playlist(PlatformId.QqMusic, "qq-commute", "通勤节奏", "QQ 音乐 Preview", "ms-appx:///Assets/Home/playlist-sea.jpg", 36),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.QqMusic, "qq-summer", "夏日海风", "QQ 音乐 Preview", "ms-appx:///Assets/Home/hero-mountain-lake.jpg", 28),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.QqMusic, "qq-pop", "华语流行精选", "QQ 音乐 Preview", "ms-appx:///Assets/Home/playlist-sunset.jpg", 42),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.QqMusic, "qq-focus", "轻盈专注", "QQ 音乐 Preview", "ms-appx:///Assets/Home/playlist-forest.jpg", 30)
        ],
        [
            DiscoveryPreviewDataFactory.Playlist(PlatformId.QqMusic, $"qq-square-{category ?? "推荐"}-1", $"{category ?? "推荐"} · 清新旋律", "QQ 音乐 Preview", "ms-appx:///Assets/Home/track-sunlight.jpg", 25),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.QqMusic, $"qq-square-{category ?? "推荐"}-2", $"{category ?? "推荐"} · 今日精选", "QQ 音乐 Preview", "ms-appx:///Assets/Home/playlist-coffee.jpg", 32)
        ],
        ["推荐", "流行", "华语", "欧美", "轻音乐"],
        false,
        true,
        DateTimeOffset.Now,
        "PreviewData · 未登录，可浏览公开内容");
}
