using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Platforms;
using Beans.Windows.Rebuild.Services.Search;
using Beans.Windows.Rebuild.ViewModels;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class Phase6ASearchTests
{
    [Fact]
    public void StableIdContainsPlatformTypeAndNativeId()
    {
        var item = Item(PlatformId.QqMusic, SearchResultType.Track, "42");

        Assert.Equal("qq:track:42", item.StableId);
    }

    [Fact]
    public async Task SameNameAcrossPlatformsRemainsSeparate()
    {
        var service = Service(
            Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "qq-1", "晴天")),
            Adapter(PlatformId.NetEaseMusic, Item(PlatformId.NetEaseMusic, SearchResultType.Track, "ne-1", "晴天")));

        var response = await service.SearchAsync(new SearchQuery("晴天", IncludeLocalMusic: false), TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal([PlatformId.QqMusic, PlatformId.NetEaseMusic], response.Items.Select(item => item.Platform));
    }

    [Fact]
    public async Task SamePlatformNativeDuplicateIsRemoved()
    {
        var first = Item(PlatformId.QqMusic, SearchResultType.Track, "same", "晴天");
        var service = Service(Adapter(PlatformId.QqMusic, first, first));

        var response = await service.SearchAsync(new SearchQuery("晴天", Scope: SearchSourceScope.Qq, IncludeLocalMusic: false), TestContext.Current.CancellationToken);

        Assert.Single(response.Items);
    }

    [Fact]
    public async Task ScopeQqDoesNotCallNetEaseOrLocal()
    {
        var qq = Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "qq"));
        var netease = Adapter(PlatformId.NetEaseMusic, Item(PlatformId.NetEaseMusic, SearchResultType.Track, "netease"));
        var local = Adapter(PlatformId.Local, Item(PlatformId.Local, SearchResultType.Track, "local"));
        var service = Service(qq, netease, local);

        var response = await service.SearchAsync(new SearchQuery("x", Scope: SearchSourceScope.Qq), TestContext.Current.CancellationToken);

        Assert.Single(response.Sources);
        Assert.Equal(PlatformId.QqMusic, response.Sources[0].Platform);
        Assert.Equal(1, qq.CallCount);
        Assert.Equal(0, netease.CallCount);
        Assert.Equal(0, local.CallCount);
    }

    [Fact]
    public async Task AggregateKeepsSuccessfulSourcesWhenOneFails()
    {
        var qq = Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "qq"));
        var netease = Adapter(PlatformId.NetEaseMusic, failure: true);
        var response = await Service(qq, netease).SearchAsync(new SearchQuery("x", IncludeLocalMusic: false), TestContext.Current.CancellationToken);

        Assert.True(response.IsPartialSuccess);
        Assert.Single(response.Items);
        Assert.Contains(response.Sources, source => source.Platform == PlatformId.NetEaseMusic && source.State == SearchSourceState.Error);
    }

    [Fact]
    public async Task LocalWithoutCatalogReturnsCatalogUnavailableWithoutScanning()
    {
        var adapter = new LocalMusicSearchAdapter(new UnavailableLocalMusicSearchCatalog());
        var response = await adapter.SearchAsync(new SearchQuery("x", Scope: SearchSourceScope.Local), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.CatalogUnavailable, response.State);
        Assert.Empty(response.Items);
        Assert.Contains("添加本地音乐目录", response.SafeMessage);
    }

    [Fact]
    public async Task ReleaseStubDoesNotReturnPreviewResults()
    {
        var adapter = new SearchPreviewAdapter(PlatformId.NetEaseMusic);
        var response = await adapter.SearchAsync(new SearchQuery("周杰伦"), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Preview, response.State);
        Assert.Equal(SearchDataOrigin.Preview, response.DataOrigin);
        Assert.NotEmpty(response.Items);
    }

    [Fact]
    public async Task DebugStubPreviewIsExplicitAndNotPlayable()
    {
        var adapter = new SearchPreviewAdapter(PlatformId.NetEaseMusic);
        var response = await adapter.SearchAsync(new SearchQuery("周杰伦"), TestContext.Current.CancellationToken);

        Assert.Equal(SearchSourceState.Preview, response.State);
        Assert.All(response.Items, item =>
        {
            Assert.Equal(SearchDataOrigin.Preview, item.DataOrigin);
            Assert.False(item.IsPlayable);
        });
    }

    [Fact]
    public async Task SuggestionsArePreviewScopedAndDeduplicated()
    {
        var service = Service(new SearchPreviewAdapter(PlatformId.NetEaseMusic));
        var suggestions = await service.GetSuggestionsAsync(new SearchSuggestionQuery("周杰伦", SearchSourceScope.NetEase), TestContext.Current.CancellationToken);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, suggestion => Assert.Equal(PlatformId.NetEaseMusic, suggestion.Platform));
    }

    [Fact]
    public async Task SearchViewModelSuppressesIdenticalRequest()
    {
        var adapter = Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "qq"));
        using var viewModel = new SearchViewModel(Service(adapter));
        await viewModel.InitializeAsync("x");
        var calls = adapter.CallCount;
        await viewModel.SearchAsync();

        Assert.Equal(calls, adapter.CallCount);
    }

    [Fact]
    public async Task SearchViewModelDebouncesSuggestions()
    {
        var adapter = new CountingAdapter(PlatformId.QqMusic, [Item(PlatformId.QqMusic, SearchResultType.Track, "qq")]);
        using var viewModel = new SearchViewModel(Service(adapter));
        await viewModel.UpdateDraftAsync("周杰伦");

        Assert.Equal(1, adapter.SuggestionCallCount);
        Assert.NotEmpty(viewModel.Suggestions);
    }

    [Fact]
    public async Task SearchViewModelCancelsLateQueryAndKeepsNewestResult()
    {
        var service = new DelayedSearchService();
        using var viewModel = new SearchViewModel(service);
        var first = viewModel.InitializeAsync("old");
        await service.OldStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = viewModel.InitializeAsync("new");
        service.ReleaseOld.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal("new", viewModel.QueryText);
        Assert.Equal("new", viewModel.Results.Single().Title);
    }

    [Fact]
    public async Task FilterChangesStayWithinSelectedSource()
    {
        var qq = Adapter(PlatformId.QqMusic, Item(PlatformId.QqMusic, SearchResultType.Track, "track"), Item(PlatformId.QqMusic, SearchResultType.Album, "album"));
        using var viewModel = new SearchViewModel(Service(qq));
        await viewModel.InitializeAsync("x");
        await viewModel.SetFilterAsync(SearchResultFilter.Albums);

        Assert.Equal(SearchResultFilter.Albums, viewModel.Filter);
        Assert.All(viewModel.Results, item => Assert.Equal(SearchResultType.Album, item.ResultType));
    }

    private static MusicSearchService Service(params IPlatformSearchAdapter[] adapters) => new(adapters);

    private static CountingAdapter Adapter(PlatformId platform, params SearchResultItem[] items) => new(platform, items);

    private static CountingAdapter Adapter(PlatformId platform, SearchResultItem? item = null, SearchResultItem? item2 = null, bool failure = false)
    {
        var values = new[] { item, item2 }.Where(value => value is not null).Cast<SearchResultItem>().ToArray();
        return new CountingAdapter(platform, values, failure);
    }

    private static SearchResultItem Item(PlatformId platform, SearchResultType type, string nativeId, string? title = null) => new(
        type, platform, nativeId, $"{platform.ToStableId()}:{type.ToString().ToLowerInvariant()}:{nativeId}", title ?? nativeId,
        Artist: "Artist", Album: "Album", SourceDisplayName: platform.ToDisplayName(), SourceBadgeText: platform.ToDisplayName(),
        DataOrigin: SearchDataOrigin.Live);

    private sealed class CountingAdapter(PlatformId platform, IReadOnlyList<SearchResultItem> values, bool failure = false) : IPlatformSearchAdapter
    {
        public PlatformId Platform { get; } = platform;
        public bool IsEnabled => true;
        public int CallCount { get; private set; }
        public int SuggestionCallCount { get; private set; }
        public Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            CallCount++;
            if (failure) throw new InvalidOperationException("fake failure");
            var filtered = query.Filter switch
            {
                SearchResultFilter.Tracks => values.Where(item => item.ResultType == SearchResultType.Track).ToArray(),
                SearchResultFilter.Albums => values.Where(item => item.ResultType == SearchResultType.Album).ToArray(),
                SearchResultFilter.Artists => values.Where(item => item.ResultType == SearchResultType.Artist).ToArray(),
                SearchResultFilter.Playlists => values.Where(item => item.ResultType == SearchResultType.Playlist).ToArray(),
                _ => values
            };
            return Task.FromResult(new SearchAdapterResponse(Platform, filtered, filtered.Count, false,
                filtered.Count == 0 ? SearchSourceState.Empty : SearchSourceState.Succeeded, SearchDataOrigin.Live, "ok", false, TimeSpan.Zero));
        }
        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken)
        {
            SuggestionCallCount++;
            return Task.FromResult<IReadOnlyList<SearchSuggestion>>([new SearchSuggestion(keyword, "建议", Platform)]);
        }
    }

    private sealed class DelayedSearchService : IMusicSearchService
    {
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AggregatedSearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            if (query.Keyword == "old")
            {
                OldStarted.TrySetResult();
                await ReleaseOld.Task;
            }
            var item = Item(PlatformId.QqMusic, SearchResultType.Track, query.Keyword, query.Keyword);
            return new(query, [item], [new SearchSourceStatus(PlatformId.QqMusic, "QQ 音乐", SearchSourceState.Succeeded, 1, TimeSpan.Zero, SearchDataOrigin.Live, "ok", false, true)], 1, TimeSpan.Zero, false, false, "ok", SearchDataOrigin.Live, DateTimeOffset.UtcNow);
        }

        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(SearchSuggestionQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
    }
}
