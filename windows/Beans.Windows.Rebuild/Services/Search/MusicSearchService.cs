using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Search;

public sealed class MusicSearchService : IMusicSearchService
{
    private readonly IReadOnlyDictionary<PlatformId, IPlatformSearchAdapter> _adapters;

    public MusicSearchService(IEnumerable<IPlatformSearchAdapter> adapters) =>
        _adapters = adapters.ToDictionary(adapter => adapter.Platform);

    public async Task<AggregatedSearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var platforms = ResolvePlatforms(query);
        var tasks = platforms.Select(platform => SearchOneAsync(platform, query, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = results.SelectMany((result, sourceIndex) => result.Response.Items
                .Select((item, itemIndex) => new SearchCandidate(item, sourceIndex, itemIndex)))
            .Where(candidate => seen.Add(DedupeKey(candidate.Item)))
            .OrderBy(candidate => MatchPriority(candidate.Item, query.NormalizedKeyword))
            .ThenBy(candidate => ResultPriority(candidate.Item.ResultType))
            .ThenBy(candidate => candidate.SourceIndex)
            .ThenBy(candidate => PlatformPriority(candidate.Item.Platform))
            .ThenBy(candidate => candidate.ItemIndex)
            .ThenBy(candidate => candidate.Item.StableId, StringComparer.Ordinal)
            .Select(candidate => candidate.Item)
            .ToArray();
        var sources = results.Select(result => result.Status).ToArray();
        var successCount = sources.Count(source => source.State is SearchSourceState.Succeeded or SearchSourceState.Preview or SearchSourceState.Empty);
        var failedCount = sources.Count(source => source.State is SearchSourceState.Error or SearchSourceState.Unsupported or SearchSourceState.CatalogUnavailable or SearchSourceState.Unauthorized or SearchSourceState.Disabled);
        var origin = AggregateOrigin(sources);
        var message = sources.Length == 0
            ? "没有启用的搜索来源"
            : items.Length > 0
                ? failedCount > 0 ? "部分来源不可用，已显示可用结果" : "搜索完成"
                : sources.Length == 1 && sources[0].State == SearchSourceState.CatalogUnavailable
                    ? sources[0].SafeMessage
                    : failedCount > 0 && successCount == 0 ? "当前来源暂时无法搜索" : "没有匹配结果";
        var loadedTimes = results.Select(result => result.Response.LoadedAt).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var loadedAt = loadedTimes.Length == 0 ? DateTimeOffset.UtcNow : loadedTimes.Max();
        return new(query, items, sources, results.Sum(result => result.Response.TotalCount), DateTimeOffset.UtcNow - started, results.Any(result => result.HasMore),
            (failedCount > 0 && successCount > 0) || results.Any(result => result.Response.IsPartialSuccess),
            message, origin, loadedAt);
    }

    public async Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(SearchSuggestionQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword)) return [];
        var platforms = ResolvePlatforms(query.Scope, query.EnabledPlatformIds, query.IncludeLocalMusic)
            .Where(platform => _adapters.ContainsKey(platform))
            .ToArray();
        var adapters = platforms.Select(platform => _adapters[platform]).Where(adapter => adapter.IsEnabled).ToArray();
        var tasks = adapters.Select(adapter => GetSuggestionsOneAsync(adapter, query.Keyword.Trim(), cancellationToken)).ToArray();
        var suggestions = await Task.WhenAll(tasks);
        return suggestions.SelectMany(items => items)
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .GroupBy(item => item.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(6)
            .ToArray();
    }

    private static async Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsOneAsync(
        IPlatformSearchAdapter adapter,
        string keyword,
        CancellationToken cancellationToken)
    {
        try { return await adapter.GetSuggestionsAsync(keyword, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task<(SearchAdapterResponse Response, SearchSourceStatus Status, bool HasMore)> SearchOneAsync(
        PlatformId platform,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(platform, out var adapter) || !adapter.IsEnabled)
        {
            var disabled = new SearchSourceStatus(platform, platform.ToDisplayName(), SearchSourceState.Disabled, 0, TimeSpan.Zero,
                SearchDataOrigin.Preview, "当前来源未启用", false, false);
            return (new(platform, [], 0, false, SearchSourceState.Disabled, SearchDataOrigin.Preview, disabled.SafeMessage, false, TimeSpan.Zero), disabled, false);
        }

        try
        {
            var response = await adapter.SearchAsync(query, cancellationToken);
            var status = new SearchSourceStatus(platform, platform.ToDisplayName(), response.State, response.Items.Count,
                response.Elapsed, response.DataOrigin, response.SafeMessage, response.RequiresAuthorization, true);
            return (response, status, response.HasMore);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            var status = new SearchSourceStatus(platform, platform.ToDisplayName(), SearchSourceState.Error, 0, TimeSpan.Zero,
                SearchDataOrigin.Live, "该来源暂时无法搜索", false, true);
            return (new(platform, [], 0, false, status.State, status.DataOrigin, status.SafeMessage, false, TimeSpan.Zero), status, false);
        }
    }

    private IReadOnlyList<PlatformId> ResolvePlatforms(SearchQuery query) => ResolvePlatforms(query.Scope, query.EnabledPlatformIds, query.IncludeLocalMusic);

    private IReadOnlyList<PlatformId> ResolvePlatforms(SearchSourceScope scope, IReadOnlyList<PlatformId>? enabled, bool includeLocal)
    {
        var online = (enabled ?? [PlatformId.QqMusic, PlatformId.NetEaseMusic])
            .Where(platform => platform is PlatformId.QqMusic or PlatformId.NetEaseMusic)
            .Where(platform => _adapters.ContainsKey(platform))
            .Distinct()
            .ToList();
        if (includeLocal && _adapters.ContainsKey(PlatformId.Local) is false && scope == SearchSourceScope.Aggregate)
            includeLocal = false;
        return scope switch
        {
            SearchSourceScope.Qq => [PlatformId.QqMusic],
            SearchSourceScope.NetEase => [PlatformId.NetEaseMusic],
            SearchSourceScope.Local => [PlatformId.Local],
            _ => online.Concat(includeLocal ? [PlatformId.Local] : []).ToArray()
        };
    }

    private static string DedupeKey(SearchResultItem item) =>
        $"{item.Platform.ToStableId()}:{item.ResultType}:{item.NativeId}";

    private static SearchDataOrigin AggregateOrigin(IReadOnlyList<SearchSourceStatus> sources)
    {
        if (sources.Any(source => source.DataOrigin == SearchDataOrigin.CacheStale)) return SearchDataOrigin.CacheStale;
        if (sources.Any(source => source.DataOrigin == SearchDataOrigin.Live)) return SearchDataOrigin.Live;
        if (sources.Any(source => source.DataOrigin == SearchDataOrigin.Preview)) return SearchDataOrigin.Preview;
        return SearchDataOrigin.CacheFresh;
    }

    private static int MatchPriority(SearchResultItem item, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return 0;
        var title = item.Title.Trim();
        if (string.Equals(title, keyword, StringComparison.OrdinalIgnoreCase)) return 0;
        if (title.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return 1;
        if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return 2;
        if (item.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return 3;
        if (item.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static int ResultPriority(SearchResultType type) => type switch
    {
        SearchResultType.Track => 0,
        SearchResultType.Album => 1,
        SearchResultType.Artist => 2,
        SearchResultType.Playlist => 3,
        _ => 4
    };

    private static int PlatformPriority(PlatformId platform) => platform switch
    {
        PlatformId.Local => 0,
        PlatformId.QqMusic => 1,
        PlatformId.NetEaseMusic => 2,
        PlatformId.Beans => 3,
        _ => 9
    };

    private readonly record struct SearchCandidate(SearchResultItem Item, int SourceIndex, int ItemIndex);
}
