using System.Text.RegularExpressions;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Search;
namespace Beans.Windows.Rebuild.Services.Anime;

public sealed class AnimeSongMatcher(IAnimeCatalogService catalog, IMusicSearchService search, AnimeDiskCache cache) : IAnimeSongMatcher
{
    public async Task<IReadOnlyList<AnimeThemeSong>> MatchAsync(AnimeSubject subject, CancellationToken cancellationToken = default, int? maxSongs = null)
    {
        // Catalog lookup already identifies the exact edition. Remote subjects
        // use Season = 0 for unknown, which must not discard their own songs.
        var songs = catalog.GetThemeSongs(subject.Id).Where(s => s.AnimeId == subject.Id);
        if (maxSongs is > 0)
        {
            // Home only needs one representative full recording. Detail and
            // search pages omit the limit and continue to load every version.
            songs = songs.OrderBy(s => s.Version == "完整版" ? 0 : 1).Take(maxSongs.Value);
        }
        var result = new List<AnimeThemeSong>();
        foreach (var song in songs)
        {
            result.AddRange(await MatchSongAsync(subject, song, cancellationToken));
        }
        return result;
    }
    public async Task<IReadOnlyList<AnimeThemeSong>> MatchSongAsync(AnimeSubject subject, AnimeThemeSong song, CancellationToken cancellationToken = default, Action<AnimeThemeSong>? updated = null)
    {
        if (!string.Equals(song.AnimeId, subject.Id, StringComparison.Ordinal)) return [];
        var platforms = new List<AnimePlatformMatch>();
        var pending = new[] { PlatformId.QqMusic, PlatformId.NetEaseMusic }
            .Select(p => MatchPlatformAsync(song, p, cancellationToken)).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                platforms.Add(await completed);
                cancellationToken.ThrowIfCancellationRequested();
                song.PlatformMatches = platforms.OrderBy(p => p.Platform == PlatformId.QqMusic ? 0 : 1).ToArray();
                song.Matches = song.PlatformMatches.Where(m => m.Result is not null).Select(m => m.Result!).ToArray();
                updated?.Invoke(song);
            }
        }
        finally { await Task.WhenAll(pending); }
        var result = new List<AnimeThemeSong> { song };
        var variants = await Task.WhenAll(new[] { PlatformId.QqMusic, PlatformId.NetEaseMusic }
            .Select(p => cache.ReadAsync<SearchResultItem[]>($"variants-v2-{song.Id}-{p}", cancellationToken)));
        foreach (var version in new[] { "TV Size", "现场", "伴奏" })
        {
            var variant = Variant(song, version);
            var matches = variants.Where(v => v is not null).SelectMany(v => v!.Value.Select(item =>
                item with { DataOrigin = cache.IsFresh(v, TimeSpan.FromDays(3)) ? SearchDataOrigin.CacheFresh : SearchDataOrigin.CacheStale }))
                .Where(item => Score(item, variant) >= .95).GroupBy(item => item.Platform).Select(g => g.First()).ToArray();
            if (matches.Length == 0) continue;
            variant.Matches = matches;
            variant.PlatformMatches = new[] { PlatformId.QqMusic, PlatformId.NetEaseMusic }.Select(p =>
                matches.FirstOrDefault(m => m.Platform == p) is { } item ? Matched(item, item.DataOrigin == SearchDataOrigin.CacheStale ? "旧缓存 · 播放时验证" : "播放时验证权限") : new AnimePlatformMatch { Platform = p }).ToArray();
            result.Add(variant);
        }
        return result;
    }
    private async Task<AnimePlatformMatch> MatchPlatformAsync(AnimeThemeSong song,PlatformId platform,CancellationToken ct)
    {
        if (song.RelationConfidence < .9 || string.IsNullOrWhiteSpace(song.RelationSource))
            return new() { Platform=platform,MatchState="未确认",SafeMessage="主题曲关系未确认" };
        var key = $"match-v2-{song.Id}-{platform}";
        var previous = await cache.ReadAsync<SearchResultItem>(key,ct);
        if (previous is not null && Score(previous.Value,song) >= .95 && cache.IsFresh(previous,TimeSpan.FromDays(3))) return Matched(previous.Value with { DataOrigin=SearchDataOrigin.CacheFresh },"缓存匹配");
        try
        {
            var response = await search.SearchAsync(new SearchQuery($"{song.Title} {song.Artist}",SearchResultFilter.Tracks,
                platform == PlatformId.QqMusic ? SearchSourceScope.Qq : SearchSourceScope.NetEase,PageSize:20,IncludeLocalMusic:false),ct);
            ct.ThrowIfCancellationRequested();
            if (!response.Sources.Any(s => s.Platform == platform && s.State is SearchSourceState.Error or SearchSourceState.Unauthorized or SearchSourceState.Disabled))
            {
                var variants = response.Items.Where(i => i.Platform == platform && i.DataOrigin != SearchDataOrigin.CacheStale &&
                    new[] { "TV Size", "现场", "伴奏" }.Any(v => Score(i, Variant(song, v)) >= .95))
                    .Select(i => i with { PlaybackUri = null, PayloadReference = null, IsPlayable = false, IsPlaying = false }).ToArray();
                await cache.WriteAsync($"variants-v2-{song.Id}-{platform}", variants, ct);
            }
            var item = response.Items.Where(i => i.Platform == platform).Select(i => (Item:i,Score:Score(i,song)))
                .Where(i => i.Score >= .95).OrderByDescending(i => i.Score).Select(i => i.Item).FirstOrDefault();
            if (item is not null)
            {
                var safe = item with { PlaybackUri=null,PayloadReference=null,IsPlayable=false,IsPlaying=false };
                if (item.DataOrigin != SearchDataOrigin.CacheStale) await cache.WriteAsync(key,safe,ct);
                return Matched(safe,item.DataOrigin == SearchDataOrigin.CacheStale ? "旧缓存 · 播放时验证" : "播放时验证权限");
            }
            var failed = response.Sources.Any(s => s.Platform == platform && s.State is SearchSourceState.Error or SearchSourceState.Unauthorized or SearchSourceState.Disabled);
            if (failed && previous is not null && Score(previous.Value,song) >= .95) return Matched(previous.Value with {DataOrigin=SearchDataOrigin.CacheStale},"离线旧缓存 · 播放时验证");
            return new() {Platform=platform,MatchState="待匹配",SafeMessage=failed ? "平台暂不可用" : "没有可靠的同版本匹配"};
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return previous is not null && Score(previous.Value,song) >= .95
                ? Matched(previous.Value with { DataOrigin=SearchDataOrigin.CacheStale },"离线旧缓存 · 播放时验证")
                : new() {Platform=platform,SafeMessage="平台暂不可用"};
        }
    }
    private static AnimePlatformMatch Matched(SearchResultItem item,string message) => new()
    { Platform=item.Platform,NativeId=item.NativeId,MatchState="已匹配",Availability="播放时验证",SafeMessage=message,Result=item };
    private static AnimeThemeSong Variant(AnimeThemeSong song, string version) => new(song.Id + "-" + version, song.AnimeId, song.Title, song.Artist, song.Type, song.Season, [], version, song.RelationConfidence)
    { ArtistAliases = song.ArtistAliases, RelationSource = song.RelationSource };

    // Require both title and credited artist. An empty artist, a cover, TV edit,
    // live recording or instrumental cannot acquire the original recording identity.
    public static double Score(SearchResultItem item,AnimeThemeSong song)
    {
        if (item.ResultType != SearchResultType.Track || item.DataOrigin == SearchDataOrigin.Preview || string.IsNullOrWhiteSpace(item.NativeId) || string.IsNullOrWhiteSpace(item.Artist)) return 0;
        if (VersionOf(item.Title + " " + item.Album) != song.Version) return 0;
        var title = Regex.Replace(item.Title,@"\s*[（(](?:完整版|Full(?:\s+Version)?)[)）]\s*$","",RegexOptions.IgnoreCase);
        if (song.Version != "完整版") title = Regex.Replace(title, @"\s*[（(](?:TV[\s.\-]*(?:Size|Ver\.?|Edit)|Live|现场|Instrumental|Off\s*Vocal|伴奏)[)）]\s*$", "", RegexOptions.IgnoreCase);
        if (AnimeCatalogService.Normalize(title) != AnimeCatalogService.Normalize(song.Title)) return 0;
        var artist = AnimeCatalogService.Normalize(item.Artist);
        if (!new[] {song.Artist}.Concat(song.ArtistAliases).Any(a => AnimeCatalogService.Normalize(a) == artist)) return 0;
        return 1;
    }
    public static string VersionOf(string value)
    {
        if (Regex.IsMatch(value,@"cover|翻唱|歌ってみた",RegexOptions.IgnoreCase)) return "翻唱";
        if (Regex.IsMatch(value,@"instrumental|off\s*vocal|karaoke|伴奏",RegexOptions.IgnoreCase)) return "伴奏";
        if (Regex.IsMatch(value,@"\blive\b|现场|ライヴ",RegexOptions.IgnoreCase)) return "现场";
        if (Regex.IsMatch(value,@"tv[\s.\-]*(size|ver|edit)|テレビサイズ|TV版",RegexOptions.IgnoreCase)) return "TV Size";
        return "完整版";
    }
}
