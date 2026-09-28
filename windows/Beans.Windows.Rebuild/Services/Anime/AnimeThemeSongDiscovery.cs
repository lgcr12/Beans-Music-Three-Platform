using System.Text.RegularExpressions;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Anime;

public sealed partial class AnimeCatalogService
{
    private static readonly IReadOnlyDictionary<string, AnimeSongType> ThemeRelations = new Dictionary<string, AnimeSongType>(StringComparer.Ordinal)
    {
        ["片头曲"] = AnimeSongType.Opening,
        ["片尾曲"] = AnimeSongType.Ending,
        ["插曲"] = AnimeSongType.Insert,
        ["角色歌"] = AnimeSongType.Character,
        ["主题曲"] = AnimeSongType.Related
    };

    public async Task<IReadOnlyList<AnimeThemeSong>> LoadThemeSongsAsync(AnimeSubject subject, CancellationToken cancellationToken = default, Action<IReadOnlyList<AnimeThemeSong>>? updated = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var known = subject.ThemeSongs.Count > 0 ? subject.ThemeSongs : GetThemeSongs(subject.Id);
        if (known.Count > 0) { updated?.Invoke(known); return known; }
        var key = $"themes-v1-{subject.BangumiId}";
        var cached = await _cache.ReadAsync<AnimeThemeSong[]>(key, cancellationToken);
        var metadata = await _cache.ReadAsync<Metadata>("subject-" + subject.BangumiId, cancellationToken);
        var previous = cached?.Value ?? metadata?.Value.ThemeSongs ?? [];
        if (previous.Count > 0)
        {
            subject.ThemeSongs = previous;
            updated?.Invoke(previous);
            if (_cache.IsFresh(cached, TimeSpan.FromDays(7)) || _cache.IsFresh(metadata, TimeSpan.FromDays(7))) return previous;
        }
        if (!subject.Id.StartsWith("bgm-", StringComparison.OrdinalIgnoreCase)) return previous;
        var discovered = await LoadRelatedThemeSongsAsync(subject, cancellationToken, updated);
        if (discovered.Count == 0) return previous;
        subject.ThemeSongs = discovered;
        await _cache.WriteAsync(key, discovered, cancellationToken);
        return discovered;
    }

    private async Task<IReadOnlyList<AnimeThemeSong>> LoadRelatedThemeSongsAsync(AnimeSubject subject, CancellationToken cancellationToken, Action<IReadOnlyList<AnimeThemeSong>>? updated = null)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"https://api.bgm.tv/v0/subjects/{subject.BangumiId}/subjects");
            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var sources = document.RootElement.EnumerateArray()
                .Where(node => node.TryGetProperty("type", out var type) && type.GetInt32() == 3)
                .Select(node =>
                {
                    var relation = ReadString(node, "relation");
                    return ThemeRelations.TryGetValue(relation, out var songType) && node.TryGetProperty("id", out var id) && id.TryGetInt32(out var musicId)
                        ? (MusicId: musicId, Relation: songType)
                        : ((int MusicId, AnimeSongType Relation)?)null;
                })
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .DistinctBy(value => value.MusicId)
                .Take(12)
                .ToArray();
            if (sources.Length == 0) return [];
            using var gate = new SemaphoreSlim(4);
            var pending = sources.Select(async source =>
            {
                await gate.WaitAsync(cancellationToken);
                try { return await ReadThemeSongAsync(subject, source.MusicId, source.Relation, cancellationToken); }
                finally { gate.Release(); }
            }).ToList();
            var songs = new List<AnimeThemeSong>();
            // Publish each verified relation without waiting for the slowest album.
            // Drain all children before disposing the concurrency gate, even on cancellation.
            try
            {
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);
                    if (await completed is { } song)
                    {
                        songs.Add(song);
                        updated?.Invoke(songs.ToArray());
                    }
                }
            }
            finally { await Task.WhenAll(pending); }
            return songs.OrderBy(s => s.Type).ThenBy(s => s.Id).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return []; }
    }

    private async Task<AnimeThemeSong?> ReadThemeSongAsync(AnimeSubject subject, int musicId, AnimeSongType type, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"https://api.bgm.tv/v0/subjects/{musicId}");
            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var rawTitle = ReadString(root, "name");
            if (rawTitle.Length == 0) return null;
            var artist = ReadInfoboxValue(root, "艺术家", "歌手", "演唱");
            var title = Regex.Replace(rawTitle, @"\s+feat\.?\s*.*$", "", RegexOptions.IgnoreCase).Trim();
            if (title.Length == 0) title = rawTitle;
            if (artist.Length == 0) artist = "未知艺人";
            var relationSource = $"https://bgm.tv/subject/{musicId}";
            return new AnimeThemeSong($"{subject.Id}-bgm-{musicId}", subject.Id, title, artist, type,
                subject.Season > 0 ? subject.Season : 1, [], relationConfidence: .95)
            { RelationSource = relationSource };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return null; }
    }

    private static string ReadInfoboxValue(JsonElement root, params string[] keys)
    {
        if (!root.TryGetProperty("infobox", out var boxes) || boxes.ValueKind != JsonValueKind.Array) return "";
        foreach (var box in boxes.EnumerateArray())
        {
            if (!keys.Contains(ReadString(box, "key"), StringComparer.Ordinal)) continue;
            if (!box.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Array) return string.Join(" / ", value.EnumerateArray().Select(item => item.TryGetProperty("v", out var v) ? v.GetString() : null).OfType<string>());
        }
        return "";
    }
}
