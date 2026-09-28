using System.Net;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Anime;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class AnimeBrowseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "artifacts", "anime-browse-tests", Guid.NewGuid().ToString("N"));
    private static AnimeSubject Subject(int id, string title, string? japanese = null) => new($"bgm-{id}", title, japanese ?? title, "", "", "", 2024) { BangumiId = id };
    private static object Row(int id, string cn = "测试动画", string name = "テスト", string date = "2025-01-10", int type = 2) =>
        new { id, name_cn = cn, name, date, type, images = new { large = "http://lain.bgm.tv/pic/cover/test.jpg" } };

    [Fact]
    public void GroupsEditionsAcrossChineseAndJapaneseWithoutKeywordOrPrefixCollisions()
    {
        var groups = AnimeSeriesGrouping.Group(new[]
        {
            Subject(1, "刀剑神域", "ソードアート・オンライン"),
            Subject(2, "刀剑神域 第二季", "ソードアート・オンラインII"),
            Subject(3, "剧场版 刀剑神域 进击篇 无星之夜的咏叹调"),
            Subject(4, "刀剑神域外传 Gun Gale Online"),
            Subject(5, "ソードアート・オンライン -インテグラル・ドメイン-"),
            Subject(6, "青春猪头少年不会梦到兔女郎学姐"),
            Subject(7, "青春猪头少年的另一部无关作品"),
            Subject(8, "刀剑乱舞")
        });
        Assert.Equal(4, groups.Count);
        Assert.Equal(5, groups.Single(g => g.Title == "刀剑神域").Subjects.Count);
        Assert.Equal(8, groups.Sum(g => g.Subjects.Count));
    }

    [Fact]
    public async Task YearGenreAndPagingAreSentToBangumiAndWrongTypesAreExcluded()
    {
        var handler = new Handler(_ => new { total = 100, data = new[] { Row(1), Row(2, date: "2024-02-01"), Row(3, type: 1) } });
        var service = new AnimeCatalogService(new(handler), new(Root()));
        var result = await service.BrowseAsync(new("", 2025, "音乐", 40), Ct);
        Assert.Single(result.Subjects);
        Assert.True(result.HasMore);
        Assert.Equal(43, result.NextOffset);
        Assert.Contains("offset=40", handler.Uris.Single());
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var filter = body.RootElement.GetProperty("filter");
        Assert.Equal(">=2025-01-01", filter.GetProperty("air_date")[0].GetString());
        Assert.Equal("<2026-01-01", filter.GetProperty("air_date")[1].GetString());
        Assert.Equal("音乐", filter.GetProperty("tag")[0].GetString());
        Assert.False(filter.GetProperty("nsfw").GetBoolean());
    }

    [Fact]
    public void JapanesePriorityPreservesSortWithinGroupsAndKeepsOtherCountries()
    {
        var china=Subject(1,"国产动画");china.Genres=["国产"];
        var english=Subject(2,"日本作品","Original English Title");english.Genres=["日本"];
        var kana=Subject(3,"擅长逃跑的殿下","逃げ上手の若君");
        var translated=Subject(4,"中国作品","テスト");translated.Genres=["中国"];
        var unknown=Subject(5,"Other","Other");
        var groups=new[]{china,english,kana,translated,unknown}.Select(s=>new AnimeSeries(s.Title,[s]));
        var sorted=AnimeIndexOrdering.JapaneseFirst(groups);
        Assert.Equal(new[]{2,3,1,4,5},sorted.Select(g=>g.Cover.BangumiId));
        Assert.False(AnimeIndexOrdering.IsJapanese(translated));
    }

    [Fact]
    public void SeriesUsesJapaneseEditionEvidenceEvenWhenCoverIsEnglish()
    {
        var unknown=Subject(1,"Other","Other");
        var cover=Subject(2,"Series","Series");
        var japanese=Subject(3,"Series 第二季","シリーズ 2");
        var sorted=AnimeIndexOrdering.JapaneseFirst([new("Other",[unknown]),new("Series",[cover,japanese])]);
        Assert.Equal("Series",sorted[0].Title);Assert.Equal(2,sorted[0].Subjects.Count);
    }

    [Fact]
    public async Task CalendarUsesOneRequestAndHandlesEmptyChineseTitleAndHttpPosters()
    {
        var handler = new Handler(_ => new[] { new { items = new[] { new { id = 800001, type = 2, name_cn = "", name = "日本語の作品", air_date = "2026-07-06", images = new { large = "http://lain.bgm.tv/pic/cover/a.jpg" } } } } });
        var service = new AnimeCatalogService(new(handler), new(Root()));
        var result = await service.BrowseAsync(new(), Ct);
        var subject = Assert.Single(result.Subjects);
        Assert.Equal("日本語の作品", subject.Title);
        Assert.Equal(2026, subject.Year);
        Assert.StartsWith("https://lain.bgm.tv/", subject.PosterUri);
        Assert.DoesNotContain("第1季", subject.YearSeasonText);
        Assert.Single(handler.Uris);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task FreshCalendarSurvivesServiceRestartAndExpiredCalendarFallsBackOffline()
    {
        var root = Root(); var clock = new Clock();
        var cache = new AnimeDiskCache(root, clock);
        var live = new Handler(_ => new[] { new { items = new[] { Row(800002) } } });
        await new AnimeCatalogService(new(live), cache).BrowseAsync(new(), Ct);
        var offline = new Handler(_ => throw new HttpRequestException());
        var restarted = new AnimeCatalogService(new(offline), cache);
        var fresh = await restarted.BrowseAsync(new(), Ct);
        Assert.Single(fresh.Subjects); Assert.Empty(offline.Uris);
        clock.Now = clock.Now.AddHours(7);
        var stale = await restarted.BrowseAsync(new(), Ct);
        Assert.Single(stale.Subjects); Assert.Single(offline.Uris);
        Assert.Contains("旧缓存", stale.Status);
    }

    [Fact]
    public async Task LocalHitStillLoadsOtherSeasonsAndRetainsCuratedMusicIdentity()
    {
        var handler = new Handler(_ => new { total = 2, data = new[] { Row(400602, "葬送的芙莉莲", "葬送のフリーレン", "2023-09-29"), Row(800003, "葬送的芙莉莲 第二季", date: "2026-01-01") } });
        var service = new AnimeCatalogService(new(handler), new(Root()));
        var result = await service.BrowseAsync(new("芙莉莲"), Ct);
        Assert.Equal(2, result.Subjects.Count);
        Assert.Equal("frieren", result.Subjects[0].Id);
        Assert.NotEmpty(service.GetThemeSongs(result.Subjects[0].Id));
        Assert.Single(handler.Uris, uri => uri.Contains("/search/subjects"));
    }

    [Fact]
    public async Task DifferentYearsAndOffsetsDoNotShareCacheEntries()
    {
        var handler = new Handler(_ => new { total = 100, data = new[] { Row(800004) } });
        var service = new AnimeCatalogService(new(handler), new(Root()));
        await service.BrowseAsync(new("", 2025), Ct);
        await service.BrowseAsync(new("", 2024), Ct);
        await service.BrowseAsync(new("", 2025, Offset: 40), Ct);
        await service.BrowseAsync(new("", 2025), Ct);
        Assert.Equal(3, handler.Uris.Count);
    }

    [Fact]
    public async Task CancellationIsPropagatedAndDoesNotPoisonNextRequest()
    {
        var handler = new Handler(_ => new { total = 0, data = Array.Empty<object>() });
        var service = new AnimeCatalogService(new(handler), new(Root()));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BrowseAsync(new("test"), cts.Token));
        Assert.Empty((await service.BrowseAsync(new("test"), Ct)).Subjects);
        Assert.Single(handler.Uris);
    }

    [Fact]
    public async Task SeriesExpandsOnlyConfirmedAnimeRelationsAndPreservesEverySeedEdition()
    {
        var handler = new Handler(uri => uri.EndsWith("/subjects/900001/subjects") ? new object[]
        {
            new { id = 900002, type = 2, relation = "续集", name_cn = "第二季", name = "Season 2" },
            new { id = 900003, type = 1, relation = "前传", name_cn = "小说", name = "Novel" },
            new { id = 900004, type = 2, relation = "相同世界观", name_cn = "独立作品", name = "Other" }
        } : uri.EndsWith("/subjects/900002") ? Row(900002) : Array.Empty<object>());
        var service = new AnimeCatalogService(new(handler), new(Root()));
        var result = await service.LoadSeriesAsync(new("系列", [Subject(900001, "第一季")]), Ct);
        Assert.Equal(new[] { 900001, 900002 }, result.Subjects.Select(s => s.BangumiId));
        Assert.DoesNotContain(handler.Uris, uri => uri.EndsWith("900003") || uri.EndsWith("900004"));
        var again = await service.LoadSeriesAsync(new("系列", [Subject(900001, "第一季")]), Ct);
        Assert.Equal(2, again.Subjects.Count); Assert.Equal(2, handler.Uris.Count);
    }

    [Fact]
    public async Task EmptySuccessfulSearchIsDifferentFromNetworkFailure()
    {
        var empty = new AnimeCatalogService(new(new Handler(_ => new { total = 0, data = Array.Empty<object>() })), new(Root()));
        Assert.DoesNotContain("网络暂不可用", (await empty.BrowseAsync(new("unknown"), Ct)).Status);
        var failed = new AnimeCatalogService(new(new Handler(_ => throw new HttpRequestException())), new(Root()));
        Assert.Contains("网络暂不可用", (await failed.BrowseAsync(new("unknown"), Ct)).Status);
    }

    [Fact]
    public async Task ConfirmedRelationsJoinDifferentTitlesWithoutJoiningOtherMedia()
    {
        var handler = new Handler(uri => uri.Contains("/search/subjects")
            ? new { total = 3, data = new[] { Row(900011, "主线动画", "Main"), Row(900012, "特别短篇", "Short"), Row(900013, "无关动画", "Other") } }
            : uri.EndsWith("/900011/subjects") ? new object[]
            {
                new { id = 900012, type = 2, relation = "番外篇", name_cn = "特别短篇", name = "Short" },
                new { id = 900013, type = 1, relation = "续集", name_cn = "同名小说", name = "Novel" }
            } : Array.Empty<object>());
        var service = new AnimeCatalogService(new(handler), new(Root()));
        var result = await service.BrowseAsync(new("主线"), Ct);
        var groups = AnimeSeriesGrouping.Group(result.Subjects);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Single(g => g.Subjects.Any(s => s.BangumiId == 900011)).Subjects.Count);
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<string, object> response) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Uris.Add(uri);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response(uri))) };
        }
    }
}
