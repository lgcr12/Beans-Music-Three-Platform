using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Anime;

// Application-scoped work survives navigation. A page cancels only its subscription.
public sealed class AnimeThemePrefetch(IAnimeCatalogService catalog, IAnimeSearchService search,
    IAnimeSongMatcher matcher, AnimeDiskCache cache, TimeProvider? clock = null)
{
    private sealed class Entry
    {
        public IReadOnlyList<AnimeThemeSong> Songs = [];
        public DateTimeOffset Expires;
        public Task<IReadOnlyList<AnimeThemeSong>>? Flight;
        public List<IProgress<IReadOnlyList<AnimeThemeSong>>> Listeners = [];
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _backgroundCount;
    private Task? _homeFlight;
    private DateTime _homeDay;
    public IReadOnlyList<AnimeSubject> HomeSubjects { get; private set; } = [];

    public IReadOnlyList<AnimeThemeSong> GetSnapshot(AnimeSubject subject)
    {
        lock (_sync) return _entries.TryGetValue(Key(subject), out var entry) ? entry.Songs : [];
    }
    private static string Key(AnimeSubject subject) => $"{subject.BangumiId}-{subject.Id}";

    public Task WarmHomeAsync()
    {
        lock (_sync)
        {
            if (_homeDay != DateTime.Today) { _homeDay = DateTime.Today; _homeFlight = null; }
            return _homeFlight ??= WarmHomeCoreAsync();
        }
    }
    private async Task WarmHomeCoreAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var saved = await cache.ReadAsync<AnimeSubject[]>($"home-prefetch-{DateTime.Today:yyyy-MM-dd}", timeout.Token);
            HomeSubjects = saved?.Value ?? catalog.GetSubjects().Take(3).ToArray();
            if (saved is not null) Prefetch(HomeSubjects);
            var history = await search.GetHistoricalTodayAsync(DateTime.Today, timeout.Token);
            if (history.Subjects.Count > 0)
            {
                HomeSubjects = history.Subjects.Take(3).ToArray();
                await cache.WriteAsync($"home-prefetch-{DateTime.Today:yyyy-MM-dd}", HomeSubjects, timeout.Token);
            }
            Prefetch(HomeSubjects);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Anime home prefetch: {ex.GetType().Name}"); }
    }

    public void Prefetch(IEnumerable<AnimeSubject> subjects)
    {
        foreach (var subject in subjects.Take(3))
        {
            lock (_sync)
            {
                var key = Key(subject);
                if (_entries.TryGetValue(key, out var existing) && (existing.Flight is not null || existing.Expires > _clock.GetUtcNow())) continue;
                if (_backgroundCount >= 3) return;
                _backgroundCount++;
            }
            _ = WarmSubjectAsync(subject);
        }
    }
    private async Task WarmSubjectAsync(AnimeSubject subject)
    {
        try { await ObserveAsync(subject); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Anime theme prefetch: {ex.GetType().Name}"); }
        finally { lock (_sync) _backgroundCount--; }
    }

    public async Task<IReadOnlyList<AnimeThemeSong>> ObserveAsync(AnimeSubject subject, CancellationToken ct = default,
        IProgress<IReadOnlyList<AnimeThemeSong>>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        Entry entry;
        Task<IReadOnlyList<AnimeThemeSong>> flight;
        TaskCompletionSource<IReadOnlyList<AnimeThemeSong>>? start = null;
        IReadOnlyList<AnimeThemeSong> initial;
        lock (_sync)
        {
            var key = Key(subject);
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new();
            if (progress is not null) entry.Listeners.Add(progress);
            initial = entry.Songs;
            if (entry.Flight is null && entry.Expires <= _clock.GetUtcNow())
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Flight = start.Task;
            }
            flight = entry.Flight ?? Task.FromResult(entry.Songs);
        }
        if (start is not null) _ = PopulateAsync(subject, entry, start);
        try { progress?.Report(initial); return await flight.WaitAsync(ct); }
        finally { if (progress is not null) lock (_sync) entry.Listeners.Remove(progress); }
    }

    private void Publish(Entry entry, IEnumerable<AnimeThemeSong> songs)
    {
        IProgress<IReadOnlyList<AnimeThemeSong>>[] listeners;
        IReadOnlyList<AnimeThemeSong> snapshot = songs.Select(Copy).ToArray();
        lock (_sync) { entry.Songs = snapshot; listeners = entry.Listeners.ToArray(); }
        foreach (var listener in listeners) listener.Report(snapshot);
    }

    private async Task PopulateAsync(AnimeSubject subject, Entry entry, TaskCompletionSource<IReadOnlyList<AnimeThemeSong>> completion)
    {
        // A slow provider cannot keep a shared background operation alive indefinitely.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        try
        {
            var key = "ready-themes-v1-" + Key(subject);
            var disk = await cache.ReadAsync<AnimeThemeSong[]>(key, ct);
            if (disk is { Value.Length: > 0 })
            {
                var ttl = disk.Value.All(s => s.Matches.Count > 0) ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(2);
                Publish(entry, disk.Value.Select(s => Cached(s, cache.IsFresh(disk, ttl))));
                if (cache.IsFresh(disk, ttl))
                {
                    lock (_sync) entry.Expires = disk.SavedAt.Add(ttl);
                    return;
                }
            }
            var songs = GetSnapshot(subject).ToDictionary(s => s.Id, Copy);
            var started = new HashSet<string>();
            var tasks = new List<Task>();
            using var limiter = new SemaphoreSlim(2);
            void Update(IEnumerable<AnimeThemeSong> batch)
            {
                lock (songs)
                {
                    foreach (var song in batch) songs[song.Id] = Copy(song);
                    Publish(entry, songs.Values);
                }
            }
            async Task Match(AnimeThemeSong song)
            {
                await limiter.WaitAsync(ct);
                try { Update(await matcher.MatchSongAsync(subject, Copy(song), ct, s => Update([s]))); }
                finally { limiter.Release(); }
            }
            void Found(IReadOnlyList<AnimeThemeSong> discovered)
            {
                foreach (var song in discovered)
                {
                    if (!started.Add(song.Id)) continue;
                    lock (songs) if (!songs.ContainsKey(song.Id)) Update([song]);
                    tasks.Add(Match(song));
                }
            }
            try { Found(await catalog.LoadThemeSongsAsync(subject, ct, Found)); }
            finally { await Task.WhenAll(tasks); }
            var snapshot = GetSnapshot(subject);
            // Do not cache transient failures as a successful six-hour result.
            var usable = snapshot.Count > 0 && snapshot.All(s => s.Matches.Count > 0);
            if (snapshot.Count > 0) await cache.WriteAsync(key, snapshot, ct);
            lock (_sync) entry.Expires = _clock.GetUtcNow().Add(usable ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        { lock (_sync) entry.Expires = _clock.GetUtcNow().AddSeconds(30); }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_sync) { completion.TrySetResult(entry.Songs); entry.Flight = null; }
        }
    }

    private static SearchResultItem Safe(SearchResultItem item) => item with { PlaybackUri = null, PayloadReference = null, IsPlayable = false, IsPlaying = false };
    private static AnimeThemeSong Copy(AnimeThemeSong song) => new(song.Id, song.AnimeId, song.Title, song.Artist, song.Type, song.Season,
        song.Matches.Select(Safe).ToArray(), song.Version, song.RelationConfidence,
        song.PlatformMatches.Select(m => new AnimePlatformMatch { Platform = m.Platform, NativeId = m.NativeId, MatchState = m.MatchState,
            Availability = m.Availability, SafeMessage = m.SafeMessage, Result = m.Result is null ? null : Safe(m.Result) }).ToArray())
        { RelationSource = song.RelationSource, ArtistAliases = song.ArtistAliases };
    private static AnimeThemeSong Cached(AnimeThemeSong song, bool fresh)
    {
        var copy = Copy(song);
        var origin = fresh ? SearchDataOrigin.CacheFresh : SearchDataOrigin.CacheStale;
        copy.Matches = copy.Matches.Select(s => s with { DataOrigin = origin }).ToArray();
        foreach (var match in copy.PlatformMatches)
            if (match.Result is not null) { match.Result = match.Result with { DataOrigin = origin }; match.SafeMessage = "缓存匹配 · 播放时验证"; }
        return copy;
    }
}
