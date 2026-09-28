using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class AnimePrefetchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AnimeDiskCache Cache(TimeProvider? clock = null) => new(Path.Combine(AppContext.BaseDirectory, "artifacts", "anime-prefetch", Guid.NewGuid().ToString("N")), clock);

    [Fact]
    public async Task StartupFetchesThemesBeforePageOpensAndPageJoinsTheSameWork()
    {
        var catalog = new Catalog();
        var matcher = new Matcher();
        var service = new AnimeThemePrefetch(catalog, catalog, matcher, Cache());
        await service.WarmHomeAsync();
        await matcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1, catalog.ThemeCalls);
        var page = service.ObserveAsync(Catalog.Subject, Ct);
        try { Assert.Equal(1, matcher.Calls); Assert.False(page.IsCompleted); }
        finally { matcher.Release.TrySetResult(); }
        Assert.Single((await page)[0].Matches);
        await service.ObserveAsync(Catalog.Subject, Ct);
        Assert.Equal(1, matcher.Calls);
    }

    [Fact]
    public async Task ClosingOnePageDoesNotCancelPrefetchOrAnotherPage()
    {
        var catalog = new Catalog();
        var matcher = new Matcher();
        var service = new AnimeThemePrefetch(catalog, catalog, matcher, Cache());
        using var pageCancellation = new CancellationTokenSource();
        var first = service.ObserveAsync(Catalog.Subject, pageCancellation.Token);
        await matcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var second = service.ObserveAsync(Catalog.Subject, Ct);
        pageCancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, matcher.Calls);
        }
        finally { matcher.Release.TrySetResult(); }
        Assert.Single((await second)[0].Matches);
    }

    [Fact]
    public async Task RestartUsesReadyCacheWithoutCatalogOrProviderAndStripsSecrets()
    {
        var catalog = new Catalog();
        var matcher = new Matcher();
        matcher.Release.TrySetResult();
        var cache = Cache();
        await new AnimeThemePrefetch(catalog, catalog, matcher, cache).ObserveAsync(Catalog.Subject, Ct);
        var nextCatalog = new Catalog();
        var nextMatcher = new Matcher();
        var songs = await new AnimeThemePrefetch(nextCatalog, nextCatalog, nextMatcher, cache).ObserveAsync(Catalog.Subject, Ct);
        Assert.Equal(0, nextCatalog.ThemeCalls);
        Assert.Equal(0, nextMatcher.Calls);
        var item = Assert.Single(Assert.Single(songs).Matches);
        Assert.Equal(SearchDataOrigin.CacheFresh, item.DataOrigin);
        Assert.Null(item.PlaybackUri);
        Assert.Null(item.PayloadReference);
        Assert.False(item.IsPlayable);
        var disk = await cache.ReadAsync<AnimeThemeSong[]>("ready-themes-v1-999-bgm-999", Ct);
        Assert.Null(disk!.Value[0].PlatformMatches[0].Result!.PlaybackUri);
    }

    [Fact]
    public async Task ExpiredReadyCacheDisplaysBeforeProviderRefreshCompletes()
    {
        var clock = new Clock();
        var cache = Cache(clock);
        var catalog = new Catalog();
        var initialMatcher = new Matcher();
        initialMatcher.Release.TrySetResult();
        await new AnimeThemePrefetch(catalog, catalog, initialMatcher, cache, clock).ObserveAsync(Catalog.Subject, Ct);
        clock.Now = clock.Now.AddHours(7);
        var delayed = new Matcher();
        var service = new AnimeThemePrefetch(catalog, catalog, delayed, cache, clock);
        var refresh = service.ObserveAsync(Catalog.Subject, Ct);
        try
        {
            await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(refresh.IsCompleted);
            Assert.Equal(SearchDataOrigin.CacheStale, service.GetSnapshot(Catalog.Subject)[0].Matches[0].DataOrigin);
        }
        finally { delayed.Release.TrySetResult(); }
        Assert.Equal(SearchDataOrigin.Live, (await refresh)[0].Matches[0].DataOrigin);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Catalog : IAnimeCatalogService, IAnimeSearchService
    {
        public static AnimeSubject Subject => new("bgm-999", "Test", "Test", "", "", "", 2025) { BangumiId = 999 };
        public int ThemeCalls;
        public IReadOnlyList<AnimeSubject> GetSubjects() => [Subject];
        public AnimeSubject? FindSubject(string keyword) => Subject;
        public IReadOnlyList<AnimeThemeSong> GetThemeSongs(string id) => [new("op", id, "Song", "Artist", AnimeSongType.Opening, 1, []) { RelationSource = "https://bgm.tv/subject/1" }];
        public Task<AnimeSubject> RefreshAsync(AnimeSubject subject, CancellationToken cancellationToken = default) => Task.FromResult(subject);
        public Task<IReadOnlyList<AnimeThemeSong>> LoadThemeSongsAsync(AnimeSubject subject, CancellationToken cancellationToken = default, Action<IReadOnlyList<AnimeThemeSong>>? updated = null)
        {
            Interlocked.Increment(ref ThemeCalls);
            var songs = GetThemeSongs(subject.Id);
            updated?.Invoke(songs);
            return Task.FromResult(songs);
        }
        public Task<AnimeDailyHistoryResult> GetHistoricalTodayAsync(DateTime date, CancellationToken cancellationToken = default) => Task.FromResult(new AnimeDailyHistoryResult([Subject], "test"));
        public Task<IReadOnlyList<AnimeSubject>> SearchSubjectsAsync(string keyword, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AnimeSubject>>([Subject]);
        public Task<AnimeBrowseResult> BrowseAsync(AnimeBrowseQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new AnimeBrowseResult([Subject], false, 0, "test"));
        public Task<AnimeSeriesResult> LoadSeriesAsync(AnimeSeries series, CancellationToken cancellationToken = default) => Task.FromResult(new AnimeSeriesResult([Subject], "test"));
        public IReadOnlyList<AnimeSubject> GetRecentPreview() => [Subject];
    }
    private sealed class Matcher : IAnimeSongMatcher
    {
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AnimeThemeSong>> MatchAsync(AnimeSubject subject, CancellationToken cancellationToken = default, int? maxSongs = null) => throw new NotSupportedException();
        public async Task<IReadOnlyList<AnimeThemeSong>> MatchSongAsync(AnimeSubject subject, AnimeThemeSong song, CancellationToken cancellationToken = default, Action<AnimeThemeSong>? updated = null)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var item = new SearchResultItem(SearchResultType.Track, PlatformId.QqMusic, "1", "qq:1", song.Title, Artist: song.Artist,
                PlaybackUri: "https://example.invalid/private", PayloadReference: "secret", IsPlayable: true, DataOrigin: SearchDataOrigin.Live);
            song.Matches = [item];
            song.PlatformMatches = [new() { Platform = PlatformId.QqMusic, Result = item, MatchState = "已匹配" }];
            updated?.Invoke(song);
            return [song];
        }
    }
}
