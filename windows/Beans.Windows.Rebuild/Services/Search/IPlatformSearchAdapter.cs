using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Search;

public interface IPlatformSearchAdapter
{
    PlatformId Platform { get; }
    bool IsEnabled { get; }
    Task<SearchAdapterResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken);
}

public interface IMusicSearchService
{
    Task<AggregatedSearchResponse> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(SearchSuggestionQuery query, CancellationToken cancellationToken);
}

public sealed record SearchSuggestionQuery(
    string Keyword,
    SearchSourceScope Scope = SearchSourceScope.Aggregate,
    IReadOnlyList<PlatformId>? EnabledPlatformIds = null,
    bool IncludeLocalMusic = true);
