using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.LocalMusic;

namespace Beans.Windows.Rebuild.Services.Search;

public sealed class LocalMusicSearchAdapter(ILocalMusicSearchCatalog catalog) : IPlatformSearchAdapter
{
    public PlatformId Platform => PlatformId.Local;
    public bool IsEnabled => true;

    public async Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        if (!catalog.IsAvailable)
            return new(Platform, [], 0, false, SearchSourceState.CatalogUnavailable, SearchDataOrigin.Live,
                "尚未添加本地音乐目录", false, DateTimeOffset.UtcNow - started);

        var entries = await catalog.SearchAsync(query.NormalizedKeyword, query.Filter, query.Offset, query.PageSize, cancellationToken);
        var items = entries.Select(ToItem).ToArray();
        return new(Platform, items, items.Length, items.Length == query.PageSize, items.Length == 0 ? SearchSourceState.Empty : SearchSourceState.Succeeded,
            SearchDataOrigin.Live, items.Length == 0 ? "本地音乐没有匹配结果" : "本地音乐搜索完成", false, DateTimeOffset.UtcNow - started);
    }

    public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken) =>
        catalog.IsAvailable ? catalog.GetSuggestionsAsync(keyword, cancellationToken) : Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);

    private static SearchResultItem ToItem(LocalCatalogEntry entry) => new(
        SearchResultType.Track,
        PlatformId.Local,
        entry.NativeId,
        $"local:track:{entry.NativeId}",
        entry.Title,
        Artist: entry.Artist,
        Album: entry.Album,
        CoverUri: entry.CoverUri ?? "ms-appx:///Assets/Home/track-sunlight.jpg",
        Duration: entry.Duration,
        Quality: entry.Quality,
        IsPlayable: File.Exists(entry.FilePath),
        RestrictionState: File.Exists(entry.FilePath) ? "" : "文件不可用",
        SourceDisplayName: PlatformId.Local.ToDisplayName(),
        SourceBadgeText: PlatformId.Local.ToDisplayName(),
        DataOrigin: SearchDataOrigin.Live,
        PayloadReference: entry.NativeId,
        PlaybackUri: File.Exists(entry.FilePath) ? entry.FilePath : null);

}

public sealed class SearchPreviewAdapter(PlatformId platform) : IPlatformSearchAdapter
{
    public PlatformId Platform { get; } = platform;
    public bool IsEnabled => true;
    public Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(SearchStubData.Create(Platform, query, true));
    public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SearchSuggestion>>(SearchStubData.Suggestions(Platform, keyword));
}

internal static class SearchStubData
{
    public static SearchAdapterResponse Create(PlatformId platform, SearchQuery query, bool previewEnabled)
    {
        var started = DateTimeOffset.UtcNow;
        if (!previewEnabled)
            return new(platform, [], 0, false, SearchSourceState.Unsupported, SearchDataOrigin.Preview,
                $"{platform.ToDisplayName()}搜索将在后续阶段接入", false, DateTimeOffset.UtcNow - started);

        var source = platform.ToDisplayName();
        var prefix = platform == PlatformId.QqMusic ? "qq" : "netease";
        IReadOnlyList<SearchResultItem> items = query.Filter is SearchResultFilter.Albums or SearchResultFilter.Artists or SearchResultFilter.Playlists
            ? []
            : new SearchResultItem[]
            {
                new SearchResultItem(SearchResultType.Track, platform, $"{prefix}-track-1", $"{platform.ToStableId()}:track:{prefix}-track-1",
                    query.NormalizedKeyword, "预览搜索结果", "周杰伦", "精选专辑", CoverUri: "ms-appx:///Assets/Home/track-sunlight.jpg",
                    Duration: TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(12), Quality: "标准", IsPlayable: false,
                    RestrictionState: "在线播放将在后续播放适配阶段开放", SourceDisplayName: source, SourceBadgeText: source,
                    DataOrigin: SearchDataOrigin.Preview),
                new SearchResultItem(SearchResultType.Playlist, platform, $"{prefix}-playlist-1", $"{platform.ToStableId()}:playlist:{prefix}-playlist-1",
                    $"{query.NormalizedKeyword} · 精选歌单", "预览结果", Creator: source,
                    CoverUri: "ms-appx:///Assets/Home/playlist-sea.jpg", Quality: "未知", SourceDisplayName: source,
                    SourceBadgeText: source, DataOrigin: SearchDataOrigin.Preview)
            };
        return new(platform, items, items.Count, false, items.Count == 0 ? SearchSourceState.Empty : SearchSourceState.Preview,
            SearchDataOrigin.Preview, $"{source} · 预览结果", false, DateTimeOffset.UtcNow - started);
    }

    public static IReadOnlyList<SearchSuggestion> Suggestions(PlatformId platform, string keyword) =>
        string.IsNullOrWhiteSpace(keyword)
            ? []
            : new[] { $"{keyword} · 热门歌曲", $"{keyword} · 精选歌单" }.Select(text => new SearchSuggestion(text, "预览建议", platform)).ToArray();
}
