using System.Collections.ObjectModel;

namespace Beans.Windows.Rebuild.Models;

public enum SearchResultFilter
{
    All,
    Tracks,
    Albums,
    Artists,
    Playlists
}

public enum SearchSourceScope
{
    Aggregate,
    Qq,
    NetEase,
    Local
}

public enum SearchResultType
{
    Track,
    Album,
    Artist,
    Playlist
}

public enum SearchDataOrigin
{
    Live,
    CacheFresh,
    CacheStale,
    Preview
}

public enum SearchSourceState
{
    Idle,
    Searching,
    Succeeded,
    Empty,
    Error,
    Unauthorized,
    Disabled,
    Unsupported,
    CatalogUnavailable,
    Preview
}

public sealed record SearchQuery(
    string Keyword,
    SearchResultFilter Filter = SearchResultFilter.All,
    SearchSourceScope Scope = SearchSourceScope.Aggregate,
    int Offset = 0,
    int PageSize = 30,
    IReadOnlyList<PlatformId>? EnabledPlatformIds = null,
    bool IncludeLocalMusic = true,
    bool IsLoadMore = false,
    long RequestGeneration = 0,
    bool ForceRefresh = false)
{
    public string NormalizedKeyword => Keyword.Trim();
    public string RequestKey =>
        $"search:{NormalizedKeyword.ToLowerInvariant()}:{Scope}:{Filter}:{Offset}:{PageSize}:{(IncludeLocalMusic ? "local" : "online")}:{string.Join(',', (EnabledPlatformIds ?? []).Select(platform => platform.ToStableId()).OrderBy(value => value, StringComparer.Ordinal))}";
}

public sealed record SearchSuggestion(string Text, string? Subtitle = null, PlatformId? Platform = null);

public sealed record SearchResultItem(
    SearchResultType ResultType,
    PlatformId Platform,
    string NativeId,
    string StableId,
    string Title,
    string Subtitle = "",
    string Artist = "",
    string Album = "",
    string Creator = "",
    string CoverUri = "",
    TimeSpan? Duration = null,
    string Quality = "未知",
    bool IsPlayable = false,
    bool IsPlaying = false,
    string RestrictionState = "",
    string SourceDisplayName = "",
    string SourceBadgeText = "",
    SearchDataOrigin DataOrigin = SearchDataOrigin.Preview,
    string? PayloadReference = null,
    string? PlaybackUri = null,
    string? ProviderMediaId = null)
{
    public string ResultTypeLabel => ResultType switch
    {
        SearchResultType.Track => "歌曲",
        SearchResultType.Album => "专辑",
        SearchResultType.Artist => "歌手",
        SearchResultType.Playlist => "歌单",
        _ => "结果"
    };

    public string DurationText => Duration is { } duration
        ? duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss")
        : "";

    public string AccessibleName => $"{Title}，{SourceDisplayName}，{ResultTypeLabel}";
    public string PlatformId => Platform.ToStableId();
}

public sealed record SearchSourceStatus(
    PlatformId Platform,
    string DisplayName,
    SearchSourceState State,
    int ResultCount,
    TimeSpan Elapsed,
    SearchDataOrigin DataOrigin,
    string SafeMessage,
    bool RequiresAuthorization,
    bool IsEnabled)
{
    public string StateText => State switch
    {
        SearchSourceState.Succeeded => DataOrigin switch
        {
            SearchDataOrigin.Preview => "预览结果",
            SearchDataOrigin.CacheStale => $"缓存旧结果 · {ResultCount} 条",
            SearchDataOrigin.CacheFresh => $"缓存结果 · {ResultCount} 条",
            _ => $"已完成 · {ResultCount} 条"
        },
        SearchSourceState.Searching => "搜索中",
        SearchSourceState.Empty => "没有匹配结果",
        SearchSourceState.Preview => "预览结果",
        SearchSourceState.CatalogUnavailable => "尚未添加本地目录",
        SearchSourceState.Unsupported => "后续阶段接入",
        SearchSourceState.Unauthorized => "需要登录",
        SearchSourceState.Disabled => "未启用",
        SearchSourceState.Error => SafeMessage,
        _ => SafeMessage
    };

    public string DisplayLine => $"{DisplayName} · {StateText}";
}

public sealed record AggregatedSearchResponse(
    SearchQuery Query,
    IReadOnlyList<SearchResultItem> Items,
    IReadOnlyList<SearchSourceStatus> Sources,
    int TotalCount,
    TimeSpan Elapsed,
    bool HasMore,
    bool IsPartialSuccess,
    string SafeMessage,
    SearchDataOrigin DataOrigin,
    DateTimeOffset LoadedAt);

public sealed record SearchAdapterResponse(
    PlatformId Platform,
    IReadOnlyList<SearchResultItem> Items,
    int TotalCount,
    bool HasMore,
    SearchSourceState State,
    SearchDataOrigin DataOrigin,
    string SafeMessage,
    bool RequiresAuthorization,
    TimeSpan Elapsed,
    bool IsPartialSuccess = false,
    PlatformErrorCode? ErrorCode = null,
    DateTimeOffset? LoadedAt = null);

public sealed record LocalCatalogEntry(
    string NativeId,
    string Title,
    string Artist,
    string Album,
    string FilePath,
    TimeSpan Duration,
    string? CoverUri = null,
    string Quality = "未知");

public interface ILocalMusicSearchCatalog
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<LocalCatalogEntry>> SearchAsync(string keyword, SearchResultFilter filter, int offset, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken);
}

public sealed class UnavailableLocalMusicSearchCatalog : ILocalMusicSearchCatalog
{
    public bool IsAvailable => false;
    public Task<IReadOnlyList<LocalCatalogEntry>> SearchAsync(string keyword, SearchResultFilter filter, int offset, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LocalCatalogEntry>>([]);
    public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
}
