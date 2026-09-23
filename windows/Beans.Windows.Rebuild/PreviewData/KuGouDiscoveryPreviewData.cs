using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.PreviewData;

public static class KuGouDiscoveryPreviewData
{
    public static PlatformDiscoveryContent Create(string? category = null) => new(
        "kugou",
        new DiscoveryHero("酷狗精选 · Preview", "公开精选、榜单与歌单广场分别缓存；真实接口适配尚未接入。", "ms-appx:///Assets/Home/playlist-sunset.jpg", "查看精选"),
        [],
        [
            new("kugou-top500-preview", "TOP500 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-sea.jpg", ["盛夏光年", "回忆留声", "微风来过"], "kugou"),
            new("kugou-new-preview", "华语新歌 · Preview", "预览更新", "ms-appx:///Assets/Home/hero-mountain-lake.jpg", ["天空之城", "下一站", "刚好遇见"], "kugou"),
            new("kugou-cover-preview", "热门翻唱 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-coffee.jpg", ["白月光", "后来以后", "初见"], "kugou")
        ],
        [
            DiscoveryPreviewDataFactory.Playlist(PlatformId.KuGouMusic, "kugou-classic", "经典怀旧", "酷狗音乐 Preview", "ms-appx:///Assets/Home/playlist-coffee.jpg", 45),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.KuGouMusic, "kugou-cover", "热门翻唱", "酷狗音乐 Preview", "ms-appx:///Assets/Home/track-sunlight.jpg", 33),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.KuGouMusic, "kugou-energy", "运动能量", "酷狗音乐 Preview", "ms-appx:///Assets/Home/playlist-sunset.jpg", 29),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.KuGouMusic, "kugou-old", "时光金曲", "酷狗音乐 Preview", "ms-appx:///Assets/Home/playlist-forest.jpg", 40)
        ],
        [
            DiscoveryPreviewDataFactory.Playlist(PlatformId.KuGouMusic, $"kugou-square-{category ?? "推荐"}-1", $"{category ?? "推荐"} · 热门歌单", "酷狗音乐 Preview", "ms-appx:///Assets/Home/playlist-sunset.jpg", 34),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.KuGouMusic, $"kugou-square-{category ?? "推荐"}-2", $"{category ?? "推荐"} · 经典收藏", "酷狗音乐 Preview", "ms-appx:///Assets/Home/playlist-coffee.jpg", 50)
        ],
        ["推荐", "流行", "经典", "运动", "轻音乐"],
        false,
        true,
        DateTimeOffset.Now,
        "PreviewData · 公开能力演示");
}
