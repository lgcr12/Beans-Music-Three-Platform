using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Search;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class Phase6ESearchIntegrationTests
{
    [Fact]
    public async Task AggregateQueriesQqNetEaseAndLocalIndependently()
    {
        var qq = Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "qq"));
        var netease = Adapter(PlatformId.NetEaseMusic, Item(PlatformId.NetEaseMusic, SearchResultType.Track, "netease"));
        var local = Adapter(PlatformId.Local, Item(PlatformId.Local, SearchResultType.Track, "local"));

        var response = await Service(qq, netease, local).SearchAsync(new SearchQuery("x"), CancellationToken.None);

        Assert.Equal([PlatformId.QqMusic, PlatformId.NetEaseMusic, PlatformId.Local], response.Sources.Select(source => source.Platform));
        Assert.Equal(1, qq.CallCount);
        Assert.Equal(1, netease.CallCount);
        Assert.Equal(1, local.CallCount);
        Assert.Equal(3, response.Items.Count);
    }

    [Fact]
    public async Task SamePlatformNativeIdDedupesButDifferentTypesAndPlatformsRemain()
    {
        var qqTrack = Item(PlatformId.QqMusic, SearchResultType.Track, "same", "same");
        var qqAlbum = Item(PlatformId.QqMusic, SearchResultType.Album, "same", "same");
        var neteaseTrack = Item(PlatformId.NetEaseMusic, SearchResultType.Track, "same", "same");
        var response = await Service(
            Adapter(PlatformId.QqMusic, qqTrack, qqTrack, qqAlbum),
            Adapter(PlatformId.NetEaseMusic, neteaseTrack)).SearchAsync(new SearchQuery("same", IncludeLocalMusic: false), CancellationToken.None);

        Assert.Equal(3, response.Items.Count);
        Assert.Equal(4, response.TotalCount);
        Assert.Contains(response.Items, item => item.Platform == PlatformId.QqMusic && item.ResultType == SearchResultType.Album);
        Assert.Contains(response.Items, item => item.Platform == PlatformId.NetEaseMusic && item.ResultType == SearchResultType.Track);
    }

    [Fact]
    public async Task AggregateKeepsAvailableResultsWhenAnotherSourceFailsOrLocalIsUnavailable()
    {
        var qq = Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "qq"));
        var netease = Adapter(PlatformId.NetEaseMusic, failure: true);
        var local = Adapter(PlatformId.Local, failure: true);

        var response = await Service(qq, netease, local).SearchAsync(new SearchQuery("x"), CancellationToken.None);

        Assert.True(response.IsPartialSuccess);
        Assert.Single(response.Items);
        Assert.Contains(response.Sources, source => source.Platform == PlatformId.NetEaseMusic && source.State == SearchSourceState.Error);
        Assert.Contains(response.Sources, source => source.Platform == PlatformId.Local && source.State == SearchSourceState.Error);
    }

    [Fact]
    public async Task CatalogUnavailableIsNotCollapsedIntoEmptyResults()
    {
        var response = await Service(new AdapterStub(PlatformId.Local, [], SearchSourceState.CatalogUnavailable,
            "尚未添加本地音乐目录")).SearchAsync(new SearchQuery("x", Scope: SearchSourceScope.Local), CancellationToken.None);

        Assert.Empty(response.Items);
        Assert.Equal(SearchSourceState.CatalogUnavailable, Assert.Single(response.Sources).State);
        Assert.Contains("本地音乐目录", response.SafeMessage);
        Assert.NotEqual("没有匹配结果", response.SafeMessage);
    }

    [Fact]
    public async Task AllEmptyAndAllFailureHaveDifferentAggregateMessages()
    {
        var empty = await Service(
            new AdapterStub(PlatformId.QqMusic, [], SearchSourceState.Empty, "没有匹配结果"),
            new AdapterStub(PlatformId.NetEaseMusic, [], SearchSourceState.Empty, "没有匹配结果"))
            .SearchAsync(new SearchQuery("x", IncludeLocalMusic: false), CancellationToken.None);
        var failed = await Service(
            Adapter(PlatformId.QqMusic, failure: true),
            Adapter(PlatformId.NetEaseMusic, failure: true))
            .SearchAsync(new SearchQuery("x", IncludeLocalMusic: false), CancellationToken.None);

        Assert.Equal("没有匹配结果", empty.SafeMessage);
        Assert.Equal("当前来源暂时无法搜索", failed.SafeMessage);
        Assert.False(empty.IsPartialSuccess);
    }

    [Fact]
    public async Task ExactAndPrefixMatchesAreStableAcrossRepeatedRequests()
    {
        var values = new[]
        {
            Item(PlatformId.QqMusic, SearchResultType.Track, "contains", "之后雨天"),
            Item(PlatformId.QqMusic, SearchResultType.Track, "exact", "雨天"),
            Item(PlatformId.QqMusic, SearchResultType.Track, "prefix", "雨天的歌")
        };
        var service = Service(Adapter(PlatformId.QqMusic, values));
        var first = await service.SearchAsync(new SearchQuery("雨天", Scope: SearchSourceScope.Qq, IncludeLocalMusic: false), CancellationToken.None);
        var second = await service.SearchAsync(new SearchQuery("雨天", Scope: SearchSourceScope.Qq, IncludeLocalMusic: false), CancellationToken.None);

        Assert.Equal(["exact", "prefix", "contains"], first.Items.Select(item => item.NativeId));
        Assert.Equal(first.Items.Select(item => item.StableId), second.Items.Select(item => item.StableId));
    }

    [Fact]
    public async Task SuggestionFailureDoesNotBlockOtherSources()
    {
        var response = await Service(
            new AdapterStub(PlatformId.QqMusic, [], SearchSourceState.Empty, "empty", suggestionFailure: true),
            new AdapterStub(PlatformId.NetEaseMusic, [], SearchSourceState.Empty, "empty", suggestions: [new SearchSuggestion("晴天", Platform: PlatformId.NetEaseMusic)]))
            .GetSuggestionsAsync(new SearchSuggestionQuery("晴天", IncludeLocalMusic: false), CancellationToken.None);

        var suggestion = Assert.Single(response);
        Assert.Equal(PlatformId.NetEaseMusic, suggestion.Platform);
    }

    private static MusicSearchService Service(params IPlatformSearchAdapter[] adapters) => new(adapters);

    private static AdapterStub Adapter(PlatformId platform, params SearchResultItem[] items) => new(platform, items);

    private static AdapterStub Adapter(PlatformId platform, SearchResultItem? item = null, SearchResultItem? item2 = null, bool failure = false)
    {
        var values = new[] { item, item2 }.Where(value => value is not null).Cast<SearchResultItem>().ToArray();
        return new AdapterStub(platform, values, failure ? SearchSourceState.Error : values.Length == 0 ? SearchSourceState.Empty : SearchSourceState.Succeeded,
            failure ? "error" : values.Length == 0 ? "empty" : "ok", throws: failure);
    }

    private static SearchResultItem Item(PlatformId platform, SearchResultType type, string nativeId, string? title = null) => new(
        type, platform, nativeId, $"{platform.ToStableId()}:{type.ToString().ToLowerInvariant()}:{nativeId}", title ?? nativeId,
        Artist: "Artist", Album: "Album", SourceDisplayName: platform.ToDisplayName(), SourceBadgeText: platform.ToDisplayName(),
        DataOrigin: SearchDataOrigin.Live);

    private sealed class AdapterStub(
        PlatformId platform,
        IReadOnlyList<SearchResultItem> items,
        SearchSourceState state = SearchSourceState.Succeeded,
        string safeMessage = "ok",
        bool throws = false,
        bool suggestionFailure = false,
        IReadOnlyList<SearchSuggestion>? suggestions = null) : IPlatformSearchAdapter
    {
        public PlatformId Platform { get; } = platform;
        public bool IsEnabled => true;
        public int CallCount { get; private set; }

        public Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            CallCount++;
            if (throws) throw new InvalidOperationException("test failure");
            var filtered = query.Filter switch
            {
                SearchResultFilter.Tracks => items.Where(item => item.ResultType == SearchResultType.Track).ToArray(),
                SearchResultFilter.Albums => items.Where(item => item.ResultType == SearchResultType.Album).ToArray(),
                SearchResultFilter.Artists => items.Where(item => item.ResultType == SearchResultType.Artist).ToArray(),
                SearchResultFilter.Playlists => items.Where(item => item.ResultType == SearchResultType.Playlist).ToArray(),
                _ => items
            };
            return Task.FromResult(new SearchAdapterResponse(Platform, filtered, filtered.Count, false, state,
                SearchDataOrigin.Live, safeMessage, false, TimeSpan.Zero));
        }

        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken)
        {
            if (suggestionFailure) throw new InvalidOperationException("suggestion failure");
            return Task.FromResult(suggestions ?? (IReadOnlyList<SearchSuggestion>)[]);
        }
    }
}
