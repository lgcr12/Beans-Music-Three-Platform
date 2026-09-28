using System.Text;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;
namespace Beans.Windows.Rebuild.Services.Anime;

public sealed partial class AnimeCatalogService : IAnimeCatalogService, IAnimeSearchService
{
    private readonly HttpClient _http;
    private readonly AnimeDiskCache _cache;
    private readonly List<AnimeSubject> _subjects;
    public AnimeCatalogService(HttpClient http, AnimeDiskCache cache)
    {
        _http = http; _cache = cache;
        _subjects = [
            Subject("frieren",400602,"葬送的芙莉莲","葬送のフリーレン",2023,"奇幻","Sousou no Frieren",["芙莉莲","Frieren"],"https://frieren-anime.jp/"),
            Subject("bocchi",328609,"孤独摇滚！","ぼっち・ざ・ろっく！",2022,"音乐","Bocchi the Rock",["孤独摇滚","Bocchi"],"https://bocchi.rocks/"),
            Subject("natsume",259,"夏目友人帐","夏目友人帳",2008,"治愈","Natsume Yuujinchou",["夏目","Natsume"],"https://www.natsume-anime.jp/"),
            Subject("jujutsu",294993,"咒术回战","呪術廻戦",2020,"热血","Jujutsu Kaisen",["咒回","JJK"],"https://jujutsukaisen.jp/"),
            Subject("violet",183878,"紫罗兰永恒花园","ヴァイオレット・エヴァーガーデン",2018,"治愈","Violet Evergarden",["紫罗兰","Violet"],"https://tv.violet-evergarden.jp/"),
            Subject("kimetsu",245665,"鬼灭之刃","鬼滅の刃",2019,"热血","Kimetsu no Yaiba",["鬼灭","Demon Slayer"],"https://kimetsu.com/anime/")
        ];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Assets","Anime","catalog-metadata.json")));
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var subject = _subjects.FirstOrDefault(s => s.BangumiId == row.GetProperty("id").GetInt32());
                if (subject is null || row.GetProperty("japaneseTitle").GetString() != subject.JapaneseTitle) continue;
                Apply(subject, new Metadata(row.GetProperty("posterUri").GetString() ?? "",row.GetProperty("synopsis").GetString() ?? "",subject.Aliases,[]));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }
    public IReadOnlyList<AnimeSubject> GetSubjects() => _subjects;
    public AnimeSubject? FindSubject(string keyword) => _subjects.FirstOrDefault(s => Matches(s, keyword));
    public async Task<IReadOnlyList<AnimeSubject>> SearchSubjectsAsync(string keyword, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = _subjects.Where(s => Matches(s, keyword)).ToArray();
        if (!string.IsNullOrWhiteSpace(keyword) && local.Length > 0) return local;
        return (await BrowseAsync(new(keyword), cancellationToken)).Subjects;
    }
    public async Task<AnimeSubject> RefreshAsync(AnimeSubject subject, CancellationToken cancellationToken = default)
    {
        await RefreshMetadataAsync(subject, cancellationToken);
        await LoadThemeSongsAsync(subject, cancellationToken);
        return subject;
    }
    public async Task<AnimeSubject> RefreshMetadataAsync(AnimeSubject subject, CancellationToken cancellationToken = default)
    {
        var key = "subject-" + subject.BangumiId;
        var cached = await _cache.ReadAsync<Metadata>(key, cancellationToken);
        if (cached is not null) Apply(subject,cached.Value);
        if (_cache.IsFresh(cached,TimeSpan.FromDays(7)))
        {
            subject.MetadataStatus = "Bangumi · 缓存资料"; return subject;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,$"https://api.bgm.tv/v0/subjects/{subject.BangumiId}");
            request.Headers.UserAgent.ParseAdd("BeansMusic/1.0");
            using var response = await _http.SendAsync(request,cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            if (root.GetProperty("type").GetInt32() != 2 || Normalize(root.GetProperty("name").GetString() ?? "") != Normalize(subject.JapaneseTitle)) return subject;
            var aliases = subject.Aliases.ToList();
            if (root.TryGetProperty("infobox",out var info)) foreach (var box in info.EnumerateArray())
                if (box.GetProperty("key").GetString() == "别名" && box.GetProperty("value").ValueKind == JsonValueKind.Array)
                    foreach (var alias in box.GetProperty("value").EnumerateArray()) if (alias.TryGetProperty("v",out var v) && v.GetString() is string name) aliases.Add(name);
            var themeSongs = KnownThemeSongs(subject);
            var metadata = new Metadata(root.GetProperty("images").GetProperty("large").GetString() ?? "",root.GetProperty("summary").GetString() ?? "",aliases.Distinct().ToArray(),themeSongs);
            Apply(subject,metadata);
            subject.MetadataStatus = "Bangumi · 最新资料";
            await _cache.WriteAsync(key,metadata,cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        { subject.MetadataStatus = cached is null ? "离线 · 精选资料" : "离线 · 旧缓存资料"; }
        return subject;
    }
    public IReadOnlyList<AnimeThemeSong> GetThemeSongs(string animeId)
    {
        var subject = _subjects.FirstOrDefault(s => s.Id == animeId);
        if (subject?.ThemeSongs is { Count: > 0 } songs) return songs;
        var known = subject is null ? [] : KnownThemeSongs(subject);
        if (known.Count > 0) { if (subject is not null) subject.ThemeSongs = known; return known; }
        return animeId switch
        {
        "frieren" => [Song("frieren-op1",animeId,"勇者","YOASOBI",AnimeSongType.Opening),Song("frieren-ed",animeId,"Anytime Anywhere","milet",AnimeSongType.Ending),Song("frieren-special",animeId,"bliss","milet",AnimeSongType.Related)],
        "bocchi" => [Song("bocchi-op",animeId,"青春コンプレックス","結束バンド",AnimeSongType.Opening,["结束乐队","Kessoku Band"]),Song("bocchi-ed",animeId,"Distortion!!","結束バンド",AnimeSongType.Ending,["结束乐队","Kessoku Band"]),Song("bocchi-insert1",animeId,"カラカラ","結束バンド",AnimeSongType.Insert,["结束乐队","Kessoku Band"]),Song("bocchi-insert2",animeId,"なにが悪い","結束バンド",AnimeSongType.Insert,["结束乐队","Kessoku Band"]),Song("bocchi-insert3",animeId,"星座になれたら","結束バンド",AnimeSongType.Insert,["结束乐队","Kessoku Band"])],
        "natsume" => [Song("natsume-op",animeId,"一斉の声","喜多修平",AnimeSongType.Opening),Song("natsume-ed",animeId,"夏夕空","中孝介",AnimeSongType.Ending)],
        "jujutsu" => [Song("jujutsu-op1",animeId,"廻廻奇譚","Eve",AnimeSongType.Opening),Song("jujutsu-ed1",animeId,"LOST IN PARADISE","ALI",AnimeSongType.Ending,["ALI feat. AKLO","ALI/AKLO"])],
        "violet" => [Song("violet-op",animeId,"Sincerely","TRUE",AnimeSongType.Opening),Song("violet-ed",animeId,"みちしるべ","茅原実里",AnimeSongType.Ending)],
        "kimetsu" => [Song("kimetsu-op",animeId,"紅蓮華","LiSA",AnimeSongType.Opening),Song("kimetsu-ed",animeId,"from the edge","FictionJunction feat. LiSA",AnimeSongType.Ending,["FictionJunction/LiSA","FictionJunction"])],
        _ => []
        };
    }
    private static IReadOnlyList<AnimeThemeSong> KnownThemeSongs(AnimeSubject subject)
    {
        var animeId = subject.Id;
        return subject.BangumiId switch
        {
            3774 => [KOnSong(animeId,"go-go-maniac","GO! GO! MANIAC","放課後ティータイム",AnimeSongType.Opening),KOnSong(animeId,"utauyo-miracle","Utauyo!! MIRACLE","放課後ティータイム",AnimeSongType.Opening),KOnSong(animeId,"listen","Listen!!","放課後ティータイム",AnimeSongType.Ending),KOnSong(animeId,"no-thank-you","NO, Thank You!","放課後ティータイム",AnimeSongType.Ending),KOnSong(animeId,"pure-pure-heart","ぴゅあぴゅあはーと","放課後ティータイム",AnimeSongType.Insert),KOnSong(animeId,"ui","U&I","放課後ティータイム",AnimeSongType.Insert)],
            1424 => [KOnSong(animeId,"cagayake-girls","Cagayake! GIRLS","放課後ティータイム",AnimeSongType.Opening),KOnSong(animeId,"dont-say-lazy","Don't say \"lazy\"","放課後ティータイム",AnimeSongType.Ending),KOnSong(animeId,"fuwa-fuwa-time","ふわふわ時間","放課後ティータイム",AnimeSongType.Insert)],
            12426 => [KOnSong(animeId,"unmei-endless","Unmei♪wa♪Endless!","放課後ティータイム",AnimeSongType.Opening),KOnSong(animeId,"singing","Singing!","放課後ティータイム",AnimeSongType.Ending)],
            _ => []
        };
    }
    private static AnimeThemeSong KOnSong(string animeId,string id,string title,string artist,AnimeSongType type) =>
        new(id,animeId,title,artist,type,1,[]) { ArtistAliases=["HTT","Hou-kago Tea Time"], RelationSource="https://bgm.tv/" };
    private AnimeThemeSong Song(string id,string animeId,string title,string artist,AnimeSongType type,IReadOnlyList<string>? aliases = null) =>
        new(id,animeId,title,artist,type,1,[]) { ArtistAliases = aliases ?? [], RelationSource = _subjects.First(s => s.Id == animeId).ExternalLinks.OfficialUrl ?? "" };
    private static AnimeSubject Subject(string id,int bangumi,string cn,string jp,int year,string genre,string romaji,IReadOnlyList<string> aliases,string official) =>
        new(id,cn,jp,"",genre,"",year,1,romaji,aliases,"","",new AnimeExternalLinks { BangumiUrl=$"https://bgm.tv/subject/{bangumi}",OfficialUrl=official },[genre]) { BangumiId=bangumi };
    private static void Apply(AnimeSubject s,Metadata m)
    {
        if (Uri.TryCreate(m.PosterUri,UriKind.Absolute,out var uri) && uri.Scheme == "https" && uri.Host == "lain.bgm.tv") s.ImageUri = s.PosterUri = s.BackgroundUri = m.PosterUri;
        s.Synopsis = m.Synopsis; s.Aliases = s.Aliases.Concat(m.Aliases).Distinct().ToArray();
        if (m.ThemeSongs is { Count: > 0 }) s.ThemeSongs = m.ThemeSongs;
    }
    public static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static bool Matches(AnimeSubject s,string keyword) => string.IsNullOrWhiteSpace(keyword) || new[] { s.Title,s.JapaneseTitle,s.RomajiTitles }.Concat(s.Aliases).Any(v => Normalize(v).Contains(Normalize(keyword),StringComparison.Ordinal));
    public sealed record Metadata(string PosterUri,string Synopsis,IReadOnlyList<string> Aliases,IReadOnlyList<AnimeThemeSong>? ThemeSongs = null);
}
