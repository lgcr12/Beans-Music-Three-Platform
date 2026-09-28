using Beans.Windows.Rebuild.Models;
namespace Beans.Windows.Rebuild.Services.Anime;
public interface IAnimeCatalogService
{
    IReadOnlyList<AnimeSubject> GetSubjects();
    AnimeSubject? FindSubject(string keyword);
    IReadOnlyList<AnimeThemeSong> GetThemeSongs(string animeId);
    Task<AnimeSubject> RefreshAsync(AnimeSubject subject, CancellationToken cancellationToken = default);
    Task<AnimeSubject> RefreshMetadataAsync(AnimeSubject subject, CancellationToken cancellationToken = default) => RefreshAsync(subject, cancellationToken);
    async Task<IReadOnlyList<AnimeThemeSong>> LoadThemeSongsAsync(AnimeSubject subject, CancellationToken cancellationToken = default, Action<IReadOnlyList<AnimeThemeSong>>? updated = null)
    {
        await RefreshAsync(subject, cancellationToken);
        var songs = GetThemeSongs(subject.Id);
        updated?.Invoke(songs);
        return songs;
    }
}
public interface IAnimeSearchService
{
    Task<IReadOnlyList<AnimeSubject>> SearchSubjectsAsync(string keyword, CancellationToken cancellationToken = default);
    Task<AnimeBrowseResult> BrowseAsync(AnimeBrowseQuery query, CancellationToken cancellationToken = default);
    Task<AnimeSeriesResult> LoadSeriesAsync(AnimeSeries series, CancellationToken cancellationToken = default);
    IReadOnlyList<AnimeSubject> GetRecentPreview();
    Task<AnimeDailyHistoryResult> GetHistoricalTodayAsync(DateTime date, CancellationToken cancellationToken = default);
}
public interface IAnimeSongMatcher
{
    Task<IReadOnlyList<AnimeThemeSong>> MatchAsync(AnimeSubject subject, CancellationToken cancellationToken = default, int? maxSongs = null);
    Task<IReadOnlyList<AnimeThemeSong>> MatchSongAsync(AnimeSubject subject, AnimeThemeSong song, CancellationToken cancellationToken = default, Action<AnimeThemeSong>? updated = null);
}
public interface IAnimeExternalLinkService
{
    Task OpenAsync(string? url, CancellationToken cancellationToken = default);
}
public sealed class AnimeExternalLinkService : IAnimeExternalLinkService
{
    public async Task OpenAsync(string? url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        await global::Windows.System.Launcher.LaunchUriAsync(uri);
    }
}
