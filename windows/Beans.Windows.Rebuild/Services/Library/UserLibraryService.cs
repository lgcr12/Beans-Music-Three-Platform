using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Library;

public sealed class UserLibraryService : IUserLibraryService, IDisposable
{
    private readonly UserLibraryOptions _options;
    private readonly UserLibraryPersistence _persistence;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UserLibraryState? _state;
    private LibraryRecoveryStatus _recoveryStatus;

    public UserLibraryService(UserLibraryOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new UserLibraryOptions();
        if (_options.MaximumFavorites <= 0) throw new ArgumentOutOfRangeException(nameof(options), "收藏上限必须大于零");
        if (_options.MaximumHistoryEntries <= 0) throw new ArgumentOutOfRangeException(nameof(options), "播放历史上限必须大于零");
        _persistence = new UserLibraryPersistence(_options.EffectiveStoragePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UserLibrarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetStateLockedAsync(cancellationToken);
            return new UserLibrarySnapshot(
                state.Favorites.OrderByDescending(item => item.AddedAt).ToArray(),
                state.History.OrderByDescending(item => item.LastPlayedAt).ToArray(),
                _recoveryStatus,
                _timeProvider.GetUtcNow());
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsFavoriteAsync(
        LibraryItemKind kind,
        PlatformId platform,
        string nativeId,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeRequired(nativeId, 256, nameof(nativeId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetStateLockedAsync(cancellationToken);
            return state.Favorites.Any(item => IsSameIdentity(item.Item, kind, platform, normalizedId));
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetFavoriteAsync(
        LibraryMediaSnapshot item,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        var safeItem = Normalize(item);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetStateLockedAsync(cancellationToken);
            var index = state.Favorites.FindIndex(entry => IsSameIdentity(entry.Item, safeItem.Kind, safeItem.Platform, safeItem.NativeId));
            if (!isFavorite)
            {
                if (index < 0) return false;
                state.Favorites.RemoveAt(index);
            }
            else
            {
                var addedAt = index >= 0 ? state.Favorites[index].AddedAt : _timeProvider.GetUtcNow();
                if (index >= 0) state.Favorites.RemoveAt(index);
                state.Favorites.Add(new FavoriteEntry(safeItem, addedAt));
                state.Favorites = state.Favorites
                    .OrderByDescending(entry => entry.AddedAt)
                    .Take(_options.MaximumFavorites)
                    .ToList();
            }
            await _persistence.SaveAsync(state, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordPlaybackAsync(
        LibraryMediaSnapshot item,
        TimeSpan playedDuration,
        CancellationToken cancellationToken = default)
    {
        var safeItem = Normalize(item);
        if (safeItem.Kind != LibraryItemKind.Track) throw new ArgumentException("只有歌曲可以写入播放历史", nameof(item));
        var playedMilliseconds = (long)Math.Clamp(playedDuration.TotalMilliseconds, 0, int.MaxValue * 1000d);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetStateLockedAsync(cancellationToken);
            var existing = state.History.FirstOrDefault(entry =>
                IsSameIdentity(entry.Item, safeItem.Kind, safeItem.Platform, safeItem.NativeId));
            if (existing is not null) state.History.Remove(existing);
            state.History.Add(new PlaybackHistoryEntry(
                safeItem,
                _timeProvider.GetUtcNow(),
                playedMilliseconds,
                existing is { PlayCount: int.MaxValue } ? int.MaxValue : (existing?.PlayCount ?? 0) + 1));
            state.History = state.History
                .OrderByDescending(entry => entry.LastPlayedAt)
                .Take(_options.MaximumHistoryEntries)
                .ToList();
            await _persistence.SaveAsync(state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearPlaybackHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetStateLockedAsync(cancellationToken);
            if (state.History.Count == 0) return;
            state.History.Clear();
            await _persistence.SaveAsync(state, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<UserLibraryState> GetStateLockedAsync(CancellationToken cancellationToken)
    {
        if (_state is not null) return _state;
        var loaded = await _persistence.LoadAsync(cancellationToken);
        _recoveryStatus = loaded.RecoveryStatus;
        _state = Sanitize(loaded.State);
        return _state;
    }

    private UserLibraryState Sanitize(UserLibraryState state)
    {
        var favorites = new Dictionary<string, FavoriteEntry>(StringComparer.Ordinal);
        foreach (var entry in state.Favorites ?? [])
        {
            if (entry?.Item is null) continue;
            try
            {
                var item = Normalize(entry.Item);
                favorites[item.StableKey] = new FavoriteEntry(item, entry.AddedAt);
            }
            catch (ArgumentException) { }
        }

        var history = new Dictionary<string, PlaybackHistoryEntry>(StringComparer.Ordinal);
        foreach (var entry in state.History ?? [])
        {
            if (entry?.Item is null) continue;
            try
            {
                var item = Normalize(entry.Item);
                if (item.Kind != LibraryItemKind.Track) continue;
                if (!history.TryGetValue(item.StableKey, out var current) || current.LastPlayedAt < entry.LastPlayedAt)
                    history[item.StableKey] = new PlaybackHistoryEntry(item, entry.LastPlayedAt,
                        Math.Max(0, entry.LastPlayedDurationMilliseconds), Math.Max(1, entry.PlayCount));
            }
            catch (ArgumentException) { }
        }

        return new UserLibraryState
        {
            SchemaVersion = 1,
            Favorites = favorites.Values.OrderByDescending(entry => entry.AddedAt).Take(_options.MaximumFavorites).ToList(),
            History = history.Values.OrderByDescending(entry => entry.LastPlayedAt).Take(_options.MaximumHistoryEntries).ToList()
        };
    }

    private static LibraryMediaSnapshot Normalize(LibraryMediaSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DataOrigin == SearchDataOrigin.Preview)
            throw new ArgumentException("预览内容不能写入真实收藏或播放历史", nameof(item));
        if (item.Platform is PlatformId.Beans or PlatformId.KuGouMusic)
            throw new ArgumentException("当前来源不支持持久化用户库", nameof(item));

        long? duration = item.DurationMilliseconds is { } value ? Math.Max(0, value) : null;
        return item with
        {
            NativeId = NormalizeRequired(item.NativeId, 256, nameof(item.NativeId)),
            Title = NormalizeRequired(item.Title, 256, nameof(item.Title)),
            Artist = NormalizeOptional(item.Artist, 256),
            Album = NormalizeOptional(item.Album, 256),
            CoverUri = NormalizeOptional(item.CoverUri, 2_048),
            DurationMilliseconds = duration,
            Quality = NormalizeOptional(item.Quality, 64)
        };
    }

    private static string NormalizeRequired(string? value, int maximumLength, string name)
    {
        var normalized = NormalizeOptional(value, maximumLength);
        if (normalized.Length == 0) throw new ArgumentException("值不能为空", name);
        return normalized;
    }

    private static string NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = new string((value ?? "").Trim().Where(character => !char.IsControl(character)).ToArray());
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static bool IsSameIdentity(LibraryMediaSnapshot item, LibraryItemKind kind, PlatformId platform, string nativeId) =>
        item.Kind == kind && item.Platform == platform && string.Equals(item.NativeId, nativeId, StringComparison.Ordinal);
}
