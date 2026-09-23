using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Search;

namespace Beans.Windows.Rebuild.ViewModels;

public sealed class SearchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMusicSearchService _search;
    private CancellationTokenSource? _requestCancellation;
    private CancellationTokenSource? _suggestionCancellation;
    private long _requestGeneration;
    private string _queryText = string.Empty;
    private SearchSourceScope _scope = SearchSourceScope.Aggregate;
    private SearchResultFilter _filter = SearchResultFilter.All;
    private bool _isSearching;
    private string _statusText = "输入关键词开始搜索";
    private string _noticeText = string.Empty;
    private string? _lastRequestKey;
    private string? _lastSuggestionKey;

    public SearchViewModel(IMusicSearchService search) => _search = search;

    public ObservableCollection<SearchResultItem> Results { get; } = [];
    public ObservableCollection<SearchSourceStatus> Sources { get; } = [];
    public ObservableCollection<SearchSuggestion> Suggestions { get; } = [];
    public string QueryText { get => _queryText; private set => Set(ref _queryText, value); }
    public SearchSourceScope Scope { get => _scope; private set => Set(ref _scope, value); }
    public SearchResultFilter Filter { get => _filter; private set => Set(ref _filter, value); }
    public bool IsSearching { get => _isSearching; private set => Set(ref _isSearching, value); }
    public bool HasResults => Results.Count > 0;
    public bool HasSources => Sources.Count > 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsPartialSuccess { get; private set; }
    public bool HasMore { get; private set; }
    public SearchDataOrigin DataOrigin { get; private set; } = SearchDataOrigin.Live;
    public bool HasCatalogUnavailable => Sources.Any(source => source.State == SearchSourceState.CatalogUnavailable);
    public bool HasPreviewResults => Sources.Any(source => source.State == SearchSourceState.Preview || source.DataOrigin == SearchDataOrigin.Preview);
    public bool HasStaleResults => Sources.Any(source => source.DataOrigin == SearchDataOrigin.CacheStale);
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string NoticeText { get => _noticeText; private set => Set(ref _noticeText, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync(string? query)
    {
        QueryText = query?.Trim() ?? string.Empty;
        await SearchAsync(forceRefresh: true);
    }

    public async Task UpdateDraftAsync(string text)
    {
        QueryText = text;
        var suggestionKey = SuggestionRequestKey(text, Scope, [PlatformId.QqMusic, PlatformId.NetEaseMusic], includeLocal: true);
        if (_lastSuggestionKey == suggestionKey && Suggestions.Count > 0) return;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
        _lastSuggestionKey = null;
        if (text.Trim().Length < 2) return;

        var cancellation = new CancellationTokenSource();
        _suggestionCancellation = cancellation;
        _lastSuggestionKey = suggestionKey;
        try
        {
            await Task.Delay(280, cancellation.Token);
            var values = await _search.GetSuggestionsAsync(new SearchSuggestionQuery(text, Scope,
                [PlatformId.QqMusic, PlatformId.NetEaseMusic], IncludeLocalMusic: true), cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _suggestionCancellation)) return;
            foreach (var suggestion in values) Suggestions.Add(suggestion);
            OnPropertyChanged(nameof(HasSuggestions));
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (ReferenceEquals(cancellation, _suggestionCancellation))
            {
                Suggestions.Clear();
                _lastSuggestionKey = null;
                OnPropertyChanged(nameof(HasSuggestions));
            }
        }
    }

    public async Task SubmitSuggestionAsync(SearchSuggestion suggestion)
    {
        await UpdateDraftAsync(suggestion.Text);
        await SearchAsync(forceRefresh: true);
    }

    public async Task SearchAsync(bool forceRefresh = false)
    {
        var keyword = QueryText.Trim();
        if (keyword.Length == 0)
        {
            CancelRequests();
            _lastRequestKey = null;
            _lastSuggestionKey = null;
            Results.Clear();
            Sources.Clear();
            StatusText = "输入关键词开始搜索";
            IsPartialSuccess = false;
            HasMore = false;
            DataOrigin = SearchDataOrigin.Live;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(HasCatalogUnavailable));
            OnPropertyChanged(nameof(HasPreviewResults));
            OnPropertyChanged(nameof(HasStaleResults));
            return;
        }

        var requestKey = new SearchQuery(keyword, Filter, Scope, EnabledPlatformIds: [PlatformId.QqMusic, PlatformId.NetEaseMusic], IncludeLocalMusic: true).RequestKey;
        if (!forceRefresh && _lastRequestKey == requestKey) return;
        var query = new SearchQuery(keyword, Filter, Scope, EnabledPlatformIds: [PlatformId.QqMusic, PlatformId.NetEaseMusic], IncludeLocalMusic: true,
            RequestGeneration: ++_requestGeneration, ForceRefresh: forceRefresh);
        _lastRequestKey = query.RequestKey;
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        IsSearching = true;
        NoticeText = string.Empty;
        StatusText = $"正在搜索“{keyword}”";

        try
        {
            var response = await _search.SearchAsync(query, cancellation.Token);
            if (query.RequestGeneration != _requestGeneration || cancellation.IsCancellationRequested) return;
            Results.Clear();
            foreach (var item in response.Items) Results.Add(item);
            Sources.Clear();
            foreach (var source in response.Sources) Sources.Add(source);
            IsPartialSuccess = response.IsPartialSuccess;
            HasMore = response.HasMore;
            DataOrigin = response.DataOrigin;
            StatusText = BuildStatusText(response);
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(HasCatalogUnavailable));
            OnPropertyChanged(nameof(HasPreviewResults));
            OnPropertyChanged(nameof(HasStaleResults));
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (query.RequestGeneration != _requestGeneration) return;
            Results.Clear();
            Sources.Clear();
            IsPartialSuccess = false;
            HasMore = false;
            DataOrigin = SearchDataOrigin.Live;
            StatusText = "搜索暂时无法完成，请稍后重试";
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasSources));
            OnPropertyChanged(nameof(HasCatalogUnavailable));
            OnPropertyChanged(nameof(HasPreviewResults));
            OnPropertyChanged(nameof(HasStaleResults));
        }
        finally
        {
            if (query.RequestGeneration == _requestGeneration) IsSearching = false;
        }
    }

    public async Task SetScopeAsync(SearchSourceScope scope)
    {
        if (Scope == scope) return;
        Scope = scope;
        Suggestions.Clear();
        _lastSuggestionKey = null;
        OnPropertyChanged(nameof(HasSuggestions));
        await SearchAsync(forceRefresh: true);
    }

    public async Task SetFilterAsync(SearchResultFilter filter)
    {
        if (Filter == filter) return;
        Filter = filter;
        await SearchAsync(forceRefresh: true);
    }

    public void SetNotice(string message) => NoticeText = message;

    public void LeavePage()
    {
        CancelRequests();
        _lastRequestKey = null;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void CancelRequests()
    {
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = null;
        _lastSuggestionKey = null;
        IsSearching = false;
    }

    private static string BuildStatusText(AggregatedSearchResponse response)
    {
        if (response.Items.Count > 0)
        {
            if (response.IsPartialSuccess) return "部分来源不可用，已显示可用结果";
            if (response.DataOrigin == SearchDataOrigin.CacheStale) return "网络不可用，正在显示上次搜索结果";
            if (IsPreviewResponse(response)) return "当前显示预览结果";
            return $"找到 {response.TotalCount} 个结果 · 用时 {response.Elapsed.TotalMilliseconds:0} ms";
        }

        if (response.DataOrigin == SearchDataOrigin.CacheStale) return "网络不可用，正在显示上次搜索结果";
        if (IsPreviewResponse(response)) return "当前显示预览结果";
        return response.SafeMessage;
    }

    private static bool IsPreviewResponse(AggregatedSearchResponse response) =>
        response.DataOrigin == SearchDataOrigin.Preview && response.Sources.Any(source =>
            source.State == SearchSourceState.Preview ||
            source.DataOrigin == SearchDataOrigin.Preview &&
            (source.State == SearchSourceState.Succeeded || source.State == SearchSourceState.Empty));

    private static string SuggestionRequestKey(string keyword, SearchSourceScope scope, IReadOnlyList<PlatformId> platforms, bool includeLocal) =>
        $"suggest:{keyword.Trim().ToLowerInvariant()}:{scope}:{(includeLocal ? "local" : "online")}:{string.Join(',', platforms.Select(platform => platform.ToStableId()).OrderBy(value => value, StringComparer.Ordinal))}";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => CancelRequests();
}
