using System.Text;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Anime;

public sealed record AnimeDailyHistoryResult(IReadOnlyList<AnimeSubject> Subjects, string Status, bool IsStale = false);

public sealed partial class AnimeCatalogService
{
    private async Task<IReadOnlyList<AnimeSubject>> SearchHistoryCandidatesAsync(int year, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, "https://api.bgm.tv/v0/search/subjects?limit=24&offset=0");
        var filter = new Dictionary<string, object>
        {
            ["type"] = new[] { 2 },
            ["nsfw"] = false,
            ["air_date"] = new[] { $">={year}-01-01", $"<{year + 1}-01-01" }
        };
        request.Content = new StringContent(JsonSerializer.Serialize(new { keyword = "", sort = "heat", filter }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(ParseRemoteSubject).OfType<AnimeSubject>().ToArray();
    }

    private async Task<IReadOnlyList<(int SubjectId, string AirDate)>> ReadEpisodeDatesAsync(int subjectId, CancellationToken ct)
    {
        // Bangumi's list endpoints may reject a large limit. Read the small,
        // documented page size and continue until the server returns fewer
        // rows. Most series finish in the first request, while long running
        // shows remain safe to scan.
        const int pageSize = 50;
        var result = new List<(int SubjectId, string AirDate)>();
        for (var offset = 0; ; offset += pageSize)
        {
            using var request = NewRequest(HttpMethod.Get, $"https://api.bgm.tv/v0/episodes?subject_id={subjectId}&limit={pageSize}&offset={offset}");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var rows = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [];
            result.AddRange(rows.Select(row => (SubjectId: subjectId, AirDate: ReadString(row, "airdate")))
                .Where(item => item.AirDate.Length >= 10));
            if (rows.Length < pageSize) break;
        }
        return result;
    }

    public async Task<AnimeDailyHistoryResult> GetHistoricalTodayAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var firstYear = date.Year - 9;
        var key = $"daily-history-v1-{date:MM-dd}-{firstYear}-{date.Year}";
        var cached = await _cache.ReadAsync<AnimeSubject[]>(key, cancellationToken);
        if (_cache.IsFresh(cached, TimeSpan.FromDays(7)))
            return new(cached!.Value.Select(Register).ToArray(), $"历史放送 · {firstYear}—{date.Year} · 已缓存");

        try
        {
            var candidateBatches = await Task.WhenAll(Enumerable.Range(firstYear, 10)
                .Select(year => SearchHistoryCandidatesAsync(year, cancellationToken)));
            var candidates = candidateBatches.SelectMany(items => items)
                .DistinctBy(item => item.BangumiId).ToArray();
            using var gate = new SemaphoreSlim(8, 8);
            var matchedIds = await Task.WhenAll(candidates.Select(async subject =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var dates = await ReadEpisodeDatesAsync(subject.BangumiId, cancellationToken);
                    return dates.Any(item => DateTime.TryParse(item.AirDate, out var aired) && aired.Month == date.Month && aired.Day == date.Day)
                        ? subject.BangumiId : 0;
                }
                catch (HttpRequestException) { return 0; }
                catch (JsonException) { return 0; }
                finally { gate.Release(); }
            }));
            var ids = matchedIds.Where(id => id > 0).ToHashSet();
            var subjects = candidates.Where(subject => ids.Contains(subject.BangumiId))
                .OrderByDescending(subject => subject.Year).ThenBy(subject => subject.Title)
                .Take(12).Select(Register).ToArray();
            await _cache.WriteAsync(key, subjects, cancellationToken);
            return new(subjects, $"历史放送 · {firstYear}—{date.Year}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            if (cached is not null) return new(cached.Value.Select(Register).ToArray(), $"网络暂不可用 · 历史放送旧缓存", true);
            return new([], "历史放送暂时不可用", true);
        }
    }
}
