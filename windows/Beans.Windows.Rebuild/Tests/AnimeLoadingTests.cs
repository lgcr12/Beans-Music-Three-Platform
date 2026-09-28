using System.Net;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Beans.Windows.Rebuild.Services.Search;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class AnimeLoadingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AnimeDiskCache Cache() => new(Path.Combine(AppContext.BaseDirectory, "artifacts", "anime-loading", Guid.NewGuid().ToString("N")));
    private static AnimeSubject Subject() => new("bgm-999", "测试", "テスト", "", "", "", 2025) { BangumiId = 999 };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    [Fact]
    public async Task ThemeDiscoveryPublishesFastSongBeforeSlowAlbumAndCachesAcrossRestart()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/999/subjects")) return Json(new[]
            {
                new { id = 101, type = 3, relation = "片头曲" },
                new { id = 102, type = 3, relation = "片尾曲" }
            });
            if (path.EndsWith("/102")) await release.Task.WaitAsync(ct);
            Assert.True(path.EndsWith("/101") || path.EndsWith("/102"), "Theme discovery must not wait for subject metadata.");
            return Json(new { name = path.EndsWith("/101") ? "Opening" : "Ending", infobox = new[] { new { key = "艺术家", value = "Artist" } } });
        });
        var cache = Cache();
        var catalog = new AnimeCatalogService(new(handler), cache);
        var loading = catalog.LoadThemeSongsAsync(Subject(), Ct, songs => first.TrySetResult(songs[0].Title));
        try
        {
            Assert.Equal("Opening", await first.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.False(loading.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(2, (await loading).Count);
        var offline = new AnimeCatalogService(new(new AsyncHandler((_, _) => throw new InvalidOperationException("Cache hit must not use the network"))), cache);
        Assert.Equal(2, (await offline.LoadThemeSongsAsync(Subject(), Ct)).Count);
    }

    [Fact]
    public async Task ThemeDiscoveryUsesExistingSubjectCacheWithoutMetadataRequest()
    {
        var cache = Cache();
        var subject = Subject();
        var song = new AnimeThemeSong("op", subject.Id, "Opening", "Artist", AnimeSongType.Opening, 1, []) { RelationSource = "https://bgm.tv/subject/101" };
        await cache.WriteAsync("subject-999", new AnimeCatalogService.Metadata("", "", [], [song]), Ct);
        var catalog = new AnimeCatalogService(new(new AsyncHandler((_, _) => throw new InvalidOperationException("No network expected"))), cache);
        Assert.Equal("Opening", Assert.Single(await catalog.LoadThemeSongsAsync(subject, Ct)).Title);
    }

    [Fact]
    public async Task FastPlatformIsPublishedBeforeSlowPlatformAndKeepsSafeCachedIdentity()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<PlatformId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new DelayedSearch(release.Task);
        var cache = Cache();
        var catalog = new AnimeCatalogService(new(new AsyncHandler((_, _) => throw new InvalidOperationException())), cache);
        var subject = catalog.GetSubjects()[0];
        var song = catalog.GetThemeSongs(subject.Id)[0];
        var matcher = new AnimeSongMatcher(catalog, search, cache);
        var loading = matcher.MatchSongAsync(subject, song, Ct, update => first.TrySetResult(update.Matches[0].Platform));
        try
        {
            Assert.Equal(PlatformId.QqMusic, await first.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.False(loading.IsCompleted);
            Assert.Single(song.Matches);
            Assert.Null(song.Matches[0].PlaybackUri);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(2, (await loading)[0].Matches.Count);
        await matcher.MatchSongAsync(subject, song, Ct);
        Assert.Equal(2, search.Calls);
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class DelayedSearch(Task release) : IMusicSearchService
    {
        public int Calls;
        public async Task<AggregatedSearchResponse> SearchAsync(SearchQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var platform = query.Scope == SearchSourceScope.Qq ? PlatformId.QqMusic : PlatformId.NetEaseMusic;
            if (platform == PlatformId.NetEaseMusic) await release.WaitAsync(ct);
            var item = new SearchResultItem(SearchResultType.Track, platform, "123", "track-123", "勇者", Artist: "YOASOBI", DataOrigin: SearchDataOrigin.Live, PlaybackUri: "https://example.invalid/temporary");
            return new(query, [item], [], 1, TimeSpan.Zero, false, false, "", SearchDataOrigin.Live, DateTimeOffset.UtcNow);
        }
        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(SearchSuggestionQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
    }
}
