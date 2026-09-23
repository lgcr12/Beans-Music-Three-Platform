using System.Collections.Concurrent;
using System.Text.Json;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.PlatformLibrary;

public sealed class PlatformLibraryService : IPlatformLibraryService, IDisposable
{
    private readonly IReadOnlyDictionary<PlatformId, IPlatformLibraryAdapter> _adapters;
    private readonly ConcurrentDictionary<PlatformId, PlatformLibrarySnapshot> _cache = new();
    private readonly ConcurrentDictionary<PlatformId, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<PlatformPlaylistTrack>> _trackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _trackLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cachePath;
    private readonly bool _diskPersistenceEnabled;
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private bool _diskLoaded;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PlatformLibraryService(IEnumerable<IPlatformLibraryAdapter> adapters, string? cachePath = null)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.Platform);
        _diskPersistenceEnabled = !string.IsNullOrWhiteSpace(cachePath);
        _cachePath = cachePath ?? string.Empty;
    }

    public async Task<IReadOnlyList<PlatformLibrarySnapshot>> LoadAllAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        await EnsureDiskCacheLoadedAsync(cancellationToken);
        var platforms = new[] { PlatformId.QqMusic, PlatformId.NetEaseMusic };
        var tasks = platforms.Select(platform => LoadAsync(platform, forceRefresh, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    public async Task<PlatformLibrarySnapshot> LoadAsync(
        PlatformId platform,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureDiskCacheLoadedAsync(cancellationToken);
        if (!_adapters.TryGetValue(platform, out var adapter))
            return Unsupported(platform);

        if (forceRefresh)
        {
            foreach (var key in _trackCache.Keys.Where(key => key.StartsWith(platform.ToStableId() + ":", StringComparison.OrdinalIgnoreCase)))
                _trackCache.TryRemove(key, out _);
        }

        if (!forceRefresh && _cache.TryGetValue(platform, out var cached))
            return cached;

        var gate = _locks.GetOrAdd(platform, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(platform, out cached))
                return cached;

            try
            {
                var snapshot = await adapter.LoadAsync(forceRefresh, cancellationToken);
                if (snapshot.Platform != platform)
                    return Failure(platform, "平台资料库返回了不匹配的来源");

                if (snapshot.State is PlatformLibraryState.Succeeded or PlatformLibraryState.Empty or PlatformLibraryState.Partial)
                {
                    _cache[platform] = snapshot;
                    await SaveDiskCacheAsync(cancellationToken);
                }
                return snapshot;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (_cache.TryGetValue(platform, out cached))
                    return cached with
                    {
                        State = PlatformLibraryState.Partial,
                        DataOrigin = SearchDataOrigin.CacheStale,
                        SafeMessage = "平台暂时不可用，正在显示上次加载的歌单",
                        IsPartialSuccess = true
                    };
                return Failure(platform, "平台歌单暂时无法加载");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlatformPlaylistTrack>> LoadPlaylistTracksAsync(
        PlatformUserPlaylist playlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        await EnsureDiskCacheLoadedAsync(cancellationToken);
        if (!_adapters.TryGetValue(playlist.Platform, out var adapter))
            return [];
        var key = TrackCacheKey(playlist);
        if (_trackCache.TryGetValue(key, out var cached)) return cached;
        var gate = _trackLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_trackCache.TryGetValue(key, out cached)) return cached;
            var tracks = await adapter.LoadPlaylistTracksAsync(playlist, cancellationToken);
            _trackCache[key] = tracks;
            await SaveDiskCacheAsync(cancellationToken);
            return tracks;
        }
        finally { gate.Release(); }
    }

    private static string TrackCacheKey(PlatformUserPlaylist playlist) =>
        $"{playlist.Platform.ToStableId()}:{playlist.NativeKind}:{playlist.NativeId}";

    private static PlatformLibrarySnapshot Unsupported(PlatformId platform) => new(
        platform,
        Probe(platform, CredentialState.NotAuthorized, "当前平台尚未接入资料库适配"),
        [],
        PlatformLibraryState.Unsupported,
        SearchDataOrigin.Live,
        DateTimeOffset.UtcNow,
        "当前平台尚未接入资料库适配");

    private static PlatformLibrarySnapshot Failure(PlatformId platform, string message) => new(
        platform,
        Probe(platform, CredentialState.Error, message),
        [],
        PlatformLibraryState.Error,
        SearchDataOrigin.Live,
        DateTimeOffset.UtcNow,
        message);

    private static PlatformProbeSnapshot Probe(PlatformId platform, CredentialState state, string message) => new(
        platform,
        state,
        platform.ToDisplayName(),
        null,
        PlatformMembershipState.Unknown,
        "会员状态未知",
        DateTimeOffset.UtcNow,
        message);

    public void Dispose()
    {
        foreach (var gate in _locks.Values) gate.Dispose();
        _locks.Clear();
        foreach (var gate in _trackLocks.Values) gate.Dispose();
        _trackLocks.Clear();
        _diskGate.Dispose();
    }

    private async Task EnsureDiskCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (!_diskPersistenceEnabled) { _diskLoaded = true; return; }
        if (_diskLoaded) return;
        await _diskGate.WaitAsync(cancellationToken);
        try
        {
            if (_diskLoaded) return;
            try
            {
                if (File.Exists(_cachePath))
                {
                    await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var state = await JsonSerializer.DeserializeAsync<PlatformLibraryDiskState>(stream, _jsonOptions, cancellationToken);
                    if (state is not null)
                    {
                        foreach (var snapshot in state.Snapshots)
                            _cache[snapshot.Platform] = snapshot;
                        foreach (var entry in state.Tracks)
                            _trackCache[entry.Key] = entry.Value;
                    }
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _diskLoaded = true;
        }
        finally { _diskGate.Release(); }
    }

    private async Task SaveDiskCacheAsync(CancellationToken cancellationToken)
    {
        if (!_diskPersistenceEnabled) return;
        await _diskGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _cachePath + ".tmp";
            var state = new PlatformLibraryDiskState(
                _cache.Values.ToArray(),
                _trackCache.Select(item => new PlatformLibraryTrackCacheEntry(item.Key, item.Value)).ToArray());
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, _cachePath, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(_cachePath + ".tmp")) File.Delete(_cachePath + ".tmp"); }
            catch (IOException) { }
            _diskGate.Release();
        }
    }

    private sealed record PlatformLibraryDiskState(
        IReadOnlyList<PlatformLibrarySnapshot> Snapshots,
        IReadOnlyList<PlatformLibraryTrackCacheEntry> Tracks);

    private sealed record PlatformLibraryTrackCacheEntry(
        string Key,
        IReadOnlyList<PlatformPlaylistTrack> Value);
}
