using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Anime;

public sealed partial class AnimeCatalogService
{
    private const int PageSize = 20;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private static readonly HashSet<string> SeriesRelations = ["前传", "续集", "番外篇", "总集篇", "不同演绎"];
    public sealed record BrowseSnapshot(AnimeSubject[] Subjects, bool HasMore, int NextOffset);
    public sealed record RelatedSubject(int Id, string Title, string JapaneseTitle);
    public IReadOnlyList<AnimeSubject> GetRecentPreview() => LoadRecentSeed();

    public async Task<AnimeBrowseResult> BrowseAsync(AnimeBrowseQuery query, CancellationToken cancellationToken = default)
    {
        // Serialize catalog mutations and repeated filter requests. UI cancels obsolete requests.
        await _catalogGate.WaitAsync(cancellationToken);
        try { return await BrowseCoreAsync(query, cancellationToken); }
        finally { _catalogGate.Release(); }
    }

    private async Task<AnimeBrowseResult> BrowseCoreAsync(AnimeBrowseQuery query, CancellationToken ct)
    {
        var keyword = query.Keyword.Trim();
        var recent = keyword.Length == 0 && query.Year is null && string.IsNullOrWhiteSpace(query.Genre);
        var key = recent ? "browse-v3-calendar" : "browse-v3-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(query with { Keyword = keyword }))));
        var cached = await _cache.ReadAsync<BrowseSnapshot>(key, ct);
        var label = recent ? "近期放送" : query.Year is int year ? $"{year} 年番剧" : "搜索结果";
        if (_cache.IsFresh(cached, recent ? TimeSpan.FromHours(6) : TimeSpan.FromDays(1)))
            return Result(cached!.Value, $"Bangumi · {label} · 已缓存");
        try
        {
            using var request = NewRequest(recent ? HttpMethod.Get : HttpMethod.Post,
                recent ? "https://api.bgm.tv/calendar" : $"https://api.bgm.tv/v0/search/subjects?limit={PageSize}&offset={Math.Max(0, query.Offset)}");
            if (!recent)
            {
                var filter = new Dictionary<string, object> { ["type"] = new[] { 2 }, ["nsfw"] = false };
                if (query.Year is int selectedYear) filter["air_date"] = new[] { $">={selectedYear}-01-01", $"<{selectedYear + 1}-01-01" };
                if (!string.IsNullOrWhiteSpace(query.Genre)) filter["tag"] = new[] { query.Genre };
                // Resolve local romaji aliases into the native title, then still load all editions.
                var known = _subjects.FirstOrDefault(s => Matches(s, keyword));
                var searchTerm = keyword.Length > 0 && known is not null && Normalize(known.RomajiTitles) == Normalize(keyword) ? known.JapaneseTitle : keyword;
                request.Content = new StringContent(JsonSerializer.Serialize(new { keyword = searchTerm, sort = keyword.Length == 0 ? "heat" : "match", filter }), Encoding.UTF8, "application/json");
            }
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            JsonElement[] rows;
            if (recent)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid calendar");
                rows = doc.RootElement.EnumerateArray().SelectMany(day => day.GetProperty("items").EnumerateArray()).ToArray();
            }
            else
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid search response");
                rows = data.EnumerateArray().ToArray();
            }
            var subjects = rows.Select(ParseRemoteSubject).OfType<AnimeSubject>().DistinctBy(s => s.BangumiId)
                .Where(s => query.Year is null || s.Year == query.Year).ToArray();
            if (keyword.Length > 0) await ResolveSearchRelationsAsync(subjects, ct);
            var next = query.Offset + rows.Length;
            var total = !recent && doc.RootElement.TryGetProperty("total", out var totalNode) && totalNode.TryGetInt32(out var count) ? count : next + (rows.Length == PageSize ? 1 : 0);
            var snapshot = new BrowseSnapshot(subjects, !recent && rows.Length > 0 && next < total, next);
            await _cache.WriteAsync(key, snapshot, ct);
            return Result(snapshot, $"Bangumi · {label}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            if (cached is not null) return Result(cached.Value, $"网络暂不可用 · {label}旧缓存");
            var seed = recent ? LoadRecentSeed() : [];
            if (seed.Length > 0) return Result(new(seed, false, seed.Length), "网络暂不可用 · 最近放送快照（2026-09-26）");
            var local = _subjects.Where(s => Matches(s, keyword) && (query.Year is null || s.Year == query.Year) &&
                (string.IsNullOrWhiteSpace(query.Genre) || s.Genres.Contains(query.Genre))).ToArray();
            // Later pages must not append unrelated local fallbacks to an existing result.
            return new(query.Offset == 0 ? local : [], false, query.Offset, "网络暂不可用 · 仅显示本地资料，请重试");
        }
    }

    private AnimeBrowseResult Result(BrowseSnapshot snapshot, string status) =>
        new(snapshot.Subjects.Select(Register).ToArray(), snapshot.HasMore, snapshot.NextOffset, status);

    private AnimeSubject Register(AnimeSubject subject)
    {
        var existing = _subjects.FirstOrDefault(s => s.BangumiId == subject.BangumiId);
        if (existing is null) { _subjects.Add(subject); return subject; }
        if (string.IsNullOrEmpty(existing.PosterUri)) existing.PosterUri = existing.ImageUri = existing.BackgroundUri = subject.PosterUri;
        if (existing.Year == 0) existing.Year = subject.Year;
        if (string.IsNullOrEmpty(existing.AirDate)) existing.AirDate = subject.AirDate;
        if (string.IsNullOrEmpty(existing.Format)) existing.Format = subject.Format;
        existing.RelatedAnimeIds = existing.RelatedAnimeIds.Concat(subject.RelatedAnimeIds).Distinct().ToArray();
        return existing;
    }

    private async Task ResolveSearchRelationsAsync(AnimeSubject[] subjects, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(AnimeSeriesGrouping.Group(subjects).Select(async series =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var anchor = series.Subjects.OrderBy(s => s.Year == 0 ? int.MaxValue : s.Year).ThenBy(s => s.BangumiId).First();
                var result = await ReadRelationsAsync(anchor.BangumiId, ct);
                anchor.RelatedAnimeIds = result.Items.Select(item => item.Id).ToArray();
            }
            finally { gate.Release(); }
        }));
    }

    private static AnimeSubject[] LoadRecentSeed()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Anime", "recent-calendar.json")));
            return doc.RootElement.GetProperty("days").EnumerateArray().SelectMany(day => day.GetProperty("items").EnumerateArray())
                .Select(ParseRemoteSubject).OfType<AnimeSubject>().DistinctBy(s => s.BangumiId).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return []; }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("BeansMusic/1.0 (https://github.com/XIaodou0416/Beans-Music)");
        return request;
    }

    private static string ReadString(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static AnimeSubject? ParseRemoteSubject(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var id) || id <= 0) return null;
        if (!row.TryGetProperty("type", out var type) || !type.TryGetInt32(out var kind) || kind != 2) return null;
        if (row.TryGetProperty("nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True) return null;
        var japanese = ReadString(row, "name");
        var title = ReadString(row, "name_cn");
        if (string.IsNullOrWhiteSpace(title)) title = japanese;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var date = ReadString(row, "date");
        if (date.Length == 0) date = ReadString(row, "air_date");
        var year = date.Length >= 4 && int.TryParse(date[..4], out var parsed) ? parsed : 0;
        var poster = row.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object ? ReadString(images, "large") : ReadString(row, "image");
        // Calendar uses HTTP image URLs; use the same trusted host over HTTPS.
        poster = Uri.TryCreate(poster, UriKind.Absolute, out var uri) && uri.Host == "lain.bgm.tv" && uri.Scheme is "http" or "https"
            ? new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri : "";
        var tags = row.TryGetProperty("tags", out var tagNodes) && tagNodes.ValueKind == JsonValueKind.Array
            ? tagNodes.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(t => ReadString(t, "name")).Where(t => t.Length > 0).Take(12).ToArray() : [];
        var subject = new AnimeSubject($"bgm-{id}", title, japanese, ReadString(row, "summary"), tags.FirstOrDefault() ?? "动画", poster, year, 0,
            japanese, [title, japanese], poster, poster, new() { BangumiUrl = $"https://bgm.tv/subject/{id}" }, tags)
        { BangumiId = id, AirDate = date, Format = ReadString(row, "platform"), MetadataStatus = "Bangumi · 公开资料" };
        if (id == 3774) subject.ThemeSongs = [new($"bgm-{id}-go-go-maniac",subject.Id,"GO! GO! MANIAC","放課後ティータイム",AnimeSongType.Opening,1,[])];
        if (id == 1424) subject.ThemeSongs = [new($"bgm-{id}-cagayake",subject.Id,"Cagayake! GIRLS","放課後ティータイム",AnimeSongType.Opening,1,[])];
        return subject;
    }

    public async Task<AnimeSeriesResult> LoadSeriesAsync(AnimeSeries series, CancellationToken cancellationToken = default)
    {
        var found = series.Subjects.ToDictionary(s => s.BangumiId);
        var relations = new List<RelatedSubject>();
        var offline = false;
        // Read direct official anime relations for the visible editions, with bounded concurrency.
        using var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(series.Subjects.Select(async subject =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return await ReadRelationsAsync(subject.BangumiId, cancellationToken); }
            finally { gate.Release(); }
        }));
        foreach (var result in results) { relations.AddRange(result.Items); offline |= result.Offline; }
        var missing = relations.DistinctBy(s => s.Id).Where(s => !found.ContainsKey(s.Id)).Take(60).ToArray();
        var loaded = await Task.WhenAll(missing.Select(async related =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return await ReadEditionAsync(related.Id, cancellationToken); }
            finally { gate.Release(); }
        }));
        foreach (var subject in loaded.OfType<AnimeSubject>()) found.TryAdd(subject.BangumiId, subject);
        offline |= loaded.Any(s => s is null);
        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            return new(found.Values.Select(Register).OrderBy(s => s.Year == 0 ? int.MaxValue : s.Year).ThenBy(s => s.AirDate).ThenBy(s => s.Title).ToArray(),
                offline ? "部分关联资料暂不可用 · 保留已加载版本" : "按首播时间排列 · Bangumi 动画关联资料");
        }
        finally { _catalogGate.Release(); }
    }

    private async Task<(RelatedSubject[] Items, bool Offline)> ReadRelationsAsync(int id, CancellationToken ct)
    {
        var key = $"relations-v1-{id}";
        var cached = await _cache.ReadAsync<RelatedSubject[]>(key, ct);
        if (_cache.IsFresh(cached, TimeSpan.FromDays(7))) return (cached!.Value, false);
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"https://api.bgm.tv/v0/subjects/{id}/subjects");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var values = doc.RootElement.EnumerateArray().Where(r => r.GetProperty("type").GetInt32() == 2 && SeriesRelations.Contains(ReadString(r, "relation")))
                .Select(r => new RelatedSubject(r.GetProperty("id").GetInt32(), ReadString(r, "name_cn"), ReadString(r, "name"))).ToArray();
            await _cache.WriteAsync(key, values, ct);
            return (values, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { return (cached?.Value ?? [], true); }
    }

    private async Task<AnimeSubject?> ReadEditionAsync(int id, CancellationToken ct)
    {
        var key = $"edition-v1-{id}";
        var cached = await _cache.ReadAsync<AnimeSubject>(key, ct);
        if (_cache.IsFresh(cached, TimeSpan.FromDays(7))) return cached!.Value;
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"https://api.bgm.tv/v0/subjects/{id}");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var subject = ParseRemoteSubject(doc.RootElement);
            if (subject is not null) await _cache.WriteAsync(key, subject, ct);
            return subject;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { return cached?.Value; }
    }
}
