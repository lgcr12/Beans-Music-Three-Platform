using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.PreviewData;

public static class NetEaseDiscoveryPreviewData
{
    public static PlatformDiscoveryContent Create(string? category = null) => new(
        "netease",
        new DiscoveryHero("登录后查看每日推荐", "网易云音乐的每日推荐属于账号专属内容；当前仅展示公开区域 PreviewData。", "ms-appx:///Assets/Home/playlist-forest.jpg", "前往账号页"),
        [],
        [
            new("netease-rise-preview", "飙升榜 · Preview", "预览更新", "ms-appx:///Assets/Home/track-sunlight.jpg", ["透明雨季", "候鸟来信", "远行之前"], "netease"),
            new("netease-hot-preview", "热歌榜 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-sunset.jpg", ["凌晨三点", "再见海岸", "旧唱片"], "netease"),
            new("netease-original-preview", "原创榜 · Preview", "预览更新", "ms-appx:///Assets/Home/playlist-forest.jpg", ["纸飞机", "落日晚风", "寂静公路"], "netease")
        ],
        [
            DiscoveryPreviewDataFactory.Playlist(PlatformId.NetEaseMusic, "netease-radar", "私人雷达 · Preview", "网易云音乐 Preview", "ms-appx:///Assets/Home/track-sunlight.jpg", 20),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.NetEaseMusic, "netease-folk", "深夜民谣", "网易云音乐 Preview", "ms-appx:///Assets/Home/playlist-coffee.jpg", 38),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.NetEaseMusic, "netease-indie", "独立新声", "网易云音乐 Preview", "ms-appx:///Assets/Home/playlist-forest.jpg", 24),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.NetEaseMusic, "netease-night", "城市入夜", "网易云音乐 Preview", "ms-appx:///Assets/Home/playlist-sunset.jpg", 31)
        ],
        [
            DiscoveryPreviewDataFactory.Playlist(PlatformId.NetEaseMusic, $"netease-square-{category ?? "推荐"}-1", $"{category ?? "推荐"} · 编辑精选", "网易云音乐 Preview", "ms-appx:///Assets/Home/playlist-sea.jpg", 27),
            DiscoveryPreviewDataFactory.Playlist(PlatformId.NetEaseMusic, $"netease-square-{category ?? "推荐"}-2", $"{category ?? "推荐"} · 小众发现", "网易云音乐 Preview", "ms-appx:///Assets/Home/playlist-forest.jpg", 18)
        ],
        ["推荐", "华语", "摇滚", "学习", "治愈"],
        false,
        true,
        DateTimeOffset.Now,
        "PreviewData · 每日推荐需要登录");
}
