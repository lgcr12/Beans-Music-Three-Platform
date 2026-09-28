using System.Net;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Beans.Windows.Rebuild.Services.Search;
using Xunit;
namespace Beans.Windows.Rebuild.Tests;

public sealed class AnimeServicesTests
{
    private static string Root()=>Path.Combine(AppContext.BaseDirectory,"artifacts","anime-tests",Guid.NewGuid().ToString("N"));
    private static AnimeThemeSong Song()=>new("test-op","test","勇者","YOASOBI",AnimeSongType.Opening,1,[]){RelationSource="https://frieren-anime.jp/"};
    private static SearchResultItem Track(string title="勇者",string artist="YOASOBI",PlatformId platform=PlatformId.QqMusic)=>new(SearchResultType.Track,platform,"123","qq:track:123",title,Artist:artist,DataOrigin:SearchDataOrigin.Live);
    [Theory]
    [InlineData("勇者","YOASOBI",1)]
    [InlineData("勇者","Other singer",0)]
    [InlineData("Another song","YOASOBI",0)]
    [InlineData("勇者","",0)]
    [InlineData("勇者 (TV Size)","YOASOBI",0)]
    [InlineData("勇者 (Live)","YOASOBI",0)]
    [InlineData("勇者 (Instrumental)","YOASOBI",0)]
    [InlineData("勇者 (Cover)","YOASOBI",0)]
    [InlineData("勇者 (完整版)","YOASOBI",1)]
    public void MatchingRequiresTitleArtistAndVersion(string title,string artist,double expected)=>Assert.Equal(expected,AnimeSongMatcher.Score(Track(title,artist),Song()));
    [Fact] public void PreviewCannotBecomeMatched()=>Assert.Equal(0,AnimeSongMatcher.Score(Track() with {DataOrigin=SearchDataOrigin.Preview},Song()));
    [Fact] public async Task PlatformFailureDoesNotHideOtherPlatformAndCacheContainsNoPlaybackUrl()
    {
        var root=Root();var cache=new AnimeDiskCache(root);var search=new FakeSearch(q=>q.Scope==SearchSourceScope.Qq?throw new HttpRequestException():[Track(platform:PlatformId.NetEaseMusic) with {PlaybackUri="https://secret.invalid/signed",PayloadReference="private",IsPlayable=true}]);
        var matcher=new AnimeSongMatcher(new FakeCatalog(),search,cache);
        var song=Assert.Single(await matcher.MatchAsync(FakeCatalog.Subject,TestContext.Current.CancellationToken));
        Assert.Equal(2,song.PlatformMatches.Count);Assert.Null(song.PlatformMatches[0].Result);Assert.NotNull(song.PlatformMatches[1].Result);
        var json=await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(root,"match-*.json")),TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret.invalid",json);Assert.DoesNotContain("private",json);Assert.False(song.Matches[0].IsPlayable);
        Assert.All(search.Queries,q=>Assert.False(q.IncludeLocalMusic));
    }
    [Fact] public async Task MatchCacheExpiresAfterThreeDaysAndFallsBackWhenOffline()
    {
        var clock=new MutableClock();var cache=new AnimeDiskCache(Root(),clock);
        await cache.WriteAsync("match-v2-test-op-QqMusic",Track(),TestContext.Current.CancellationToken);
        var search=new FakeSearch(_=>throw new HttpRequestException());var matcher=new AnimeSongMatcher(new FakeCatalog(),search,cache);
        var fresh=Assert.Single(await matcher.MatchAsync(FakeCatalog.Subject,TestContext.Current.CancellationToken));Assert.Equal(SearchDataOrigin.CacheFresh,fresh.Matches[0].DataOrigin);
        Assert.DoesNotContain(search.Queries,q=>q.Scope==SearchSourceScope.Qq);
        clock.Now=clock.Now.AddDays(4);var stale=Assert.Single(await matcher.MatchAsync(FakeCatalog.Subject,TestContext.Current.CancellationToken));
        Assert.Equal(SearchDataOrigin.CacheStale,stale.Matches[0].DataOrigin);Assert.Contains("旧缓存",stale.PlatformMatches[0].SafeMessage);
    }
    [Fact] public async Task UnconfirmedRelationshipNeverSearchesProviders()
    {
        var catalog=new FakeCatalog {Confidence=.1};var search=new FakeSearch(_=>[Track()]);var matcher=new AnimeSongMatcher(catalog,search,new(Root()));
        var result=Assert.Single(await matcher.MatchAsync(FakeCatalog.Subject,TestContext.Current.CancellationToken));Assert.Empty(result.Matches);Assert.Empty(search.Queries);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EditionSongsSurviveUnknownOrDifferentSeasonAndPlatformFailure(int season)
    {
        var subject=FakeCatalog.Subject;subject.Season=season;
        var search=new FakeSearch(_=>throw new HttpRequestException());
        var matcher=new AnimeSongMatcher(new FakeCatalog(),search,new(Root()));
        var song=Assert.Single(await matcher.MatchAsync(subject,TestContext.Current.CancellationToken));
        Assert.Equal("勇者",song.Title);
        Assert.Empty(song.Matches);
        Assert.Equal(2,song.PlatformMatches.Count);
        Assert.Equal(2,search.Queries.Count);
    }
    [Fact] public async Task CachedRemoteThemeSongsRemainVisibleAfterProviderVerification()
    {
        var cache=new AnimeDiskCache(Root());
        var songs=new AnimeThemeSong[]
        {
            new("bgm-424573-bgm-503483","bgm-424573","プランA","DISH//",AnimeSongType.Opening,1,[]){RelationSource="https://bgm.tv/subject/503483"},
            new("bgm-424573-bgm-502850","bgm-424573","鎌倉STYLE","ぼっちぼろまる",AnimeSongType.Ending,1,[]){RelationSource="https://bgm.tv/subject/502850"}
        };
        await cache.WriteAsync("subject-424573",new AnimeCatalogService.Metadata("https://lain.bgm.tv/poster.jpg","cached",[],songs),TestContext.Current.CancellationToken);
        var handler=new Handler("{}");
        var catalog=new AnimeCatalogService(new HttpClient(handler),cache);
        var subject=catalog.GetSubjects()[0];
        subject.Id="bgm-424573";subject.BangumiId=424573;subject.Season=0;
        await catalog.RefreshAsync(subject,TestContext.Current.CancellationToken);
        var search=new FakeSearch(q=>[Track(q.Keyword.StartsWith("プランA")?"プランA":"鎌倉STYLE",q.Keyword.StartsWith("プランA")?"DISH//":"ぼっちぼろまる",q.Scope==SearchSourceScope.Qq?PlatformId.QqMusic:PlatformId.NetEaseMusic)]);
        var matched=await new AnimeSongMatcher(catalog,search,cache).MatchAsync(subject,TestContext.Current.CancellationToken);
        Assert.Equal(2,matched.Count);Assert.All(matched,s=>Assert.Equal(2,s.Matches.Count));Assert.Equal(0,handler.Count);
    }
    [Fact] public async Task CancellationIsNotConvertedToEmptyMatches()
    {
        var matcher=new AnimeSongMatcher(new FakeCatalog(),new FakeSearch(_=>[Track()]),new(Root()));using var ct=new CancellationTokenSource();ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>matcher.MatchAsync(FakeCatalog.Subject,ct.Token));
    }
    [Fact] public async Task CatalogSearchSupportsChineseJapaneseAndSpacedRomaji()
    {
        var catalog=new AnimeCatalogService(new HttpClient(new Handler("{}")),new(Root()));
        foreach(var query in new[]{"芙莉莲","葬送のフリーレン","SousouNoFrieren"})Assert.Equal("frieren",Assert.Single(await catalog.SearchSubjectsAsync(query,TestContext.Current.CancellationToken)).Id);
        Assert.All(catalog.GetThemeSongs("jujutsu"),song=>Assert.DoesNotContain("青のすみか",song.Title));
        Assert.All(catalog.GetSubjects(),subject=>Assert.DoesNotContain("Assets/Home/Anime",subject.PosterUri));
    }
    [Theory]
    [InlineData("frieren",3)]
    [InlineData("bocchi",5)]
    [InlineData("natsume",2)]
    [InlineData("jujutsu",2)]
    [InlineData("violet",2)]
    [InlineData("kimetsu",2)]
    public async Task CuratedSongsKeepTheirWorkIdentityAndReachBothProviders(string animeId,int count)
    {
        var cache=new AnimeDiskCache(Root());
        var catalog=new AnimeCatalogService(new HttpClient(new Handler("{}")),cache);
        var subject=Assert.Single(catalog.GetSubjects(),s=>s.Id==animeId);
        var songs=catalog.GetThemeSongs(animeId);
        Assert.Equal(count,songs.Count);
        Assert.All(songs,s=>
        {
            Assert.Equal(animeId,s.AnimeId);
            Assert.StartsWith(animeId+"-",s.Id);
            Assert.Equal(subject.ExternalLinks.OfficialUrl,s.RelationSource);
        });
        var search=new FakeSearch(_=>throw new HttpRequestException());
        var result=await new AnimeSongMatcher(catalog,search,cache).MatchAsync(subject,TestContext.Current.CancellationToken);
        Assert.Equal(count,result.Count);
        Assert.Equal(count*2,search.Queries.Count);
        Assert.All(result,s=>Assert.Equal(2,s.PlatformMatches.Count));
    }
    [Fact] public void KOnSecondSeasonHasVerifiedThemeSongCatalog()
    {
        var catalog=new AnimeCatalogService(new HttpClient(new Handler("{}")),new(Root()));
        var subject=catalog.GetSubjects()[0];
        subject.Id="bgm-3774"; subject.BangumiId=3774; subject.Title="轻音少女 第二季"; subject.JapaneseTitle="けいおん！！";
        var songs=catalog.GetThemeSongs(subject.Id);
        Assert.Contains(songs,s=>s.Title=="GO! GO! MANIAC"&&s.Type==AnimeSongType.Opening);
        Assert.Contains(songs,s=>s.Title=="NO, Thank You!"&&s.Type==AnimeSongType.Ending);
    }
    [Fact] public async Task MetadataCacheIsSevenDaysAndRejectsWrongWork()
    {
        var clock=new MutableClock();var cache=new AnimeDiskCache(Root(),clock);
        await cache.WriteAsync("subject-400602",new AnimeCatalogService.Metadata("https://lain.bgm.tv/poster.jpg","cached",[]),TestContext.Current.CancellationToken);
        var handler=new Handler("{\"type\":1,\"name\":\"Unrelated novel\"}");var catalog=new AnimeCatalogService(new HttpClient(handler),cache);var subject=catalog.GetSubjects()[0];
        await catalog.RefreshAsync(subject,TestContext.Current.CancellationToken);Assert.Equal(0,handler.Count);Assert.Equal("cached",subject.Synopsis);
        clock.Now=clock.Now.AddDays(8);await catalog.RefreshAsync(subject,TestContext.Current.CancellationToken);Assert.Equal(1,handler.Count);Assert.Equal("cached",subject.Synopsis);
    }
    [Fact] public async Task FavoritesArePersistentIndependentSets()
    {
        var path=Path.Combine(Root(),"state.json");var session=new AnimeSession(path);Assert.Empty(session.State.Subjects);
        await session.ToggleSubjectAsync("frieren",TestContext.Current.CancellationToken);await session.ToggleSongAsync("frieren-op1",TestContext.Current.CancellationToken);await session.UpdateAsync(s=>s.ReduceMotion=true,TestContext.Current.CancellationToken);
        var reloaded=new AnimeSession(path);Assert.Contains("frieren",reloaded.State.Subjects);Assert.Contains("frieren-op1",reloaded.State.Songs);Assert.True(reloaded.State.ReduceMotion);
        await reloaded.ToggleSubjectAsync("frieren",TestContext.Current.CancellationToken);Assert.Empty(reloaded.State.Subjects);Assert.Single(reloaded.State.Songs);
    }
    [Fact] public async Task TvEditIsSeparateFromFullRecordingAndSurvivesCache()
    {
        var cache=new AnimeDiskCache(Root());
        var search=new FakeSearch(q=>[Track(platform:q.Scope==SearchSourceScope.Qq?PlatformId.QqMusic:PlatformId.NetEaseMusic),Track("勇者 (TV Size)",platform:q.Scope==SearchSourceScope.Qq?PlatformId.QqMusic:PlatformId.NetEaseMusic) with {NativeId="tv-123",PlaybackUri="https://example.invalid/audio"}]);
        var matcher=new AnimeSongMatcher(new FakeCatalog(),search,cache);
        var songs=await matcher.MatchAsync(FakeCatalog.Subject,TestContext.Current.CancellationToken);
        Assert.Equal(2,songs.Count);Assert.Equal("完整版",songs[0].Version);Assert.Equal("TV Size",songs[1].Version);
        Assert.All(songs[1].Matches,item=>{Assert.Equal("tv-123",item.NativeId);Assert.Null(item.PlaybackUri);});
        var fromCache=await matcher.MatchAsync(FakeCatalog.Subject,TestContext.Current.CancellationToken);
        Assert.Equal(2,fromCache.Count);Assert.Equal(2,search.Queries.Count);
    }
    [Fact] public async Task VariantFavoriteKeepsItsVersionWithoutProviderPayload()
    {
        var path=Path.Combine(Root(),"state.json");var session=new AnimeSession(path);var song=Song();song.Id+="-tv";song.Version="TV Size";
        await session.ToggleSongAsync(song,TestContext.Current.CancellationToken);
        var persisted=new AnimeSession(path);Assert.Equal("TV Size",persisted.State.SongDetails[song.Id].Version);
        await persisted.ToggleSongAsync(song,TestContext.Current.CancellationToken);Assert.Empty(persisted.State.SongDetails);
    }
    private sealed class MutableClock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Handler(string json):HttpMessageHandler {public int Count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c){Count++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});}}
    private sealed class FakeCatalog:IAnimeCatalogService
    {
        public double Confidence=1;
        public static AnimeSubject Subject=>new("test","Test","Test","","","",2023);
        public IReadOnlyList<AnimeSubject> GetSubjects()=>[Subject];public AnimeSubject? FindSubject(string keyword)=>Subject;
        public IReadOnlyList<AnimeThemeSong> GetThemeSongs(string id){var s=Song();s.RelationConfidence=Confidence;return [s];}
        public Task<AnimeSubject> RefreshAsync(AnimeSubject s,CancellationToken c=default)=>Task.FromResult(s);
    }
    private sealed class FakeSearch(Func<SearchQuery,IReadOnlyList<SearchResultItem>> action):IMusicSearchService
    {
        public List<SearchQuery> Queries {get;}=[];
        public Task<AggregatedSearchResponse> SearchAsync(SearchQuery q,CancellationToken c){c.ThrowIfCancellationRequested();Queries.Add(q);var items=action(q);return Task.FromResult(new AggregatedSearchResponse(q,items,[],items.Count,TimeSpan.Zero,false,false,"",SearchDataOrigin.Live,DateTimeOffset.UtcNow));}
        public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(SearchSuggestionQuery q,CancellationToken c)=>Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);
    }
}
