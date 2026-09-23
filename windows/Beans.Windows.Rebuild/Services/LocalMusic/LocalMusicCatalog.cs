using System.Security.Cryptography;
using System.Text;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.LocalMusic;

public sealed class LocalMusicCatalog : ILocalMusicCatalog, IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma" };
    private readonly LocalMusicPersistence _persistence;
    private readonly IAudioMetadataReader _metadataReader;
    private readonly ILrcFileResolver _lrcResolver;
    private readonly long _maximumFileBytes;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private CancellationTokenSource? _scanCancellation;
    private LocalMusicState _state = new([], [], []);
    private bool _loaded;

    public LocalMusicCatalog(
        IAudioMetadataReader? metadataReader = null,
        ILrcFileResolver? lrcResolver = null,
        LocalMusicCatalogOptions? options = null)
    {
        _metadataReader = metadataReader ?? new BasicAudioMetadataReader();
        _lrcResolver = lrcResolver ?? new LocalLrcFileResolver();
        var effective = options ?? new LocalMusicCatalogOptions();
        _maximumFileBytes = effective.MaximumFileBytes;
        _persistence = new LocalMusicPersistence(effective);
        ScanState = new(LocalScanStatus.Idle, null, null, 0, 0, 0, 0, 0, 0, 0, "尚未扫描本地音乐目录");
    }

    public event EventHandler<LocalScanProgress>? ProgressChanged;
    public LocalScanProgress ScanState { get; private set; }
    public bool IsAvailable => _state.Folders.Any(folder => folder.IsAvailable) && _state.Tracks.Any(track => !track.IsMissing);

    public async Task<LocalMusicFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var normalized = NormalizePath(path);
        if (normalized is null) throw new ArgumentException("目录路径无效", nameof(path));
        var existing = _state.Folders.FirstOrDefault(folder => string.Equals(folder.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var now = DateTimeOffset.UtcNow;
        var folder = new LocalMusicFolder(HashId(normalized), normalized, Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : normalized, now, null, Directory.Exists(normalized), 0, LocalScanStatus.Idle);
        _state.Folders.Add(folder);
        await SaveAsync(cancellationToken);
        return folder;
    }

    public async Task<bool> RemoveFolderAsync(string pathOrId, bool removeIndex = true, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var folder = _state.Folders.FirstOrDefault(item => item.Id == pathOrId || string.Equals(item.Path, NormalizePath(pathOrId), StringComparison.OrdinalIgnoreCase));
        if (folder is null) return false;
        _state.Folders.Remove(folder);
        if (removeIndex) _state.Tracks.RemoveAll(track => track.FolderId == folder.Id);
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<LocalMusicFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _state.Folders.Select(folder => folder with { IsAvailable = Directory.Exists(folder.Path) }).ToArray();
    }

    public async Task<LocalScanSummary> ScanAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!await _stateGate.WaitAsync(0, cancellationToken)) return new(LocalScanStatus.Scanning, 0, 0, 0, 0, _state.Tracks.Count, "本地音乐扫描已在进行");
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _scanCancellation.Token;
        var added = 0; var updated = 0; var removed = 0; var failed = 0; var processed = 0;
        try
        {
            Publish(new(LocalScanStatus.Scanning, null, null, 0, null, 0, 0, 0, 0, null, "正在建立本地音乐索引"));
            var files = new List<(LocalMusicFolder Folder, string Path)>();
            foreach (var folder in _state.Folders.ToArray())
            {
                var available = Directory.Exists(folder.Path);
                _state.Folders[_state.Folders.IndexOf(folder)] = folder with { IsAvailable = available, ScanStatus = available ? LocalScanStatus.Scanning : LocalScanStatus.Failed };
                if (!available) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        var info = new FileInfo(file);
                        if (info.Attributes.HasFlag(FileAttributes.Hidden) || !SupportedExtensions.Contains(info.Extension) || info.Length > _maximumFileBytes) continue;
                        files.Add((folder, file));
                    }
                }
                catch (UnauthorizedAccessException) { failed++; }
                catch (IOException) { failed++; }
            }
            foreach (var candidate in files)
            {
                token.ThrowIfCancellationRequested();
                processed++;
                Publish(new(LocalScanStatus.Scanning, candidate.Folder.DisplayName, candidate.Path, processed, files.Count, added, updated, removed, failed, files.Count == 0 ? null : (double)processed / files.Count, "正在读取本地音乐元数据"));
                try
                {
                    var info = new FileInfo(candidate.Path);
                    var normalized = NormalizePath(candidate.Path)!;
                    var id = StableId(normalized);
                    var existing = _state.Tracks.FirstOrDefault(track => track.Id == id);
                    if (existing is not null && !existing.IsMissing && existing.FileSize == info.Length && existing.LastWriteTimeUtc == info.LastWriteTimeUtc)
                        continue;
                    var metadata = await _metadataReader.ReadAsync(candidate.Path, token);
                    var lyrics = await _lrcResolver.ResolveAsync(candidate.Path, token);
                    var now = DateTimeOffset.UtcNow;
                    var track = new LocalTrack(id, candidate.Folder.Id, normalized, candidate.Path, info.Name, info.Extension.TrimStart('.').ToLowerInvariant(), info.Length, info.LastWriteTimeUtc,
                        metadata.Title, metadata.Artist, metadata.Album, metadata.AlbumArtist, metadata.TrackNumber, metadata.DiscNumber, metadata.Year, metadata.Duration,
                        metadata.Bitrate, metadata.SampleRate, metadata.Format, metadata.EmbeddedArtworkPath ?? "ms-appx:///Assets/Branding/beans-icon.png", lyrics.Path, lyrics.Source,
                        metadata.State, false, existing?.CreatedAt ?? now, now, existing?.LastPlayedAt, existing?.PlayCount ?? 0);
                    if (existing is null) { _state.Tracks.Add(track); added++; } else { _state.Tracks[_state.Tracks.IndexOf(existing)] = track; updated++; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failed++; }
            }
            var currentPaths = files.Select(item => NormalizePath(item.Path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < _state.Tracks.Count; index++)
            {
                var track = _state.Tracks[index];
                if (_state.Folders.Any(folder => folder.Id == track.FolderId) && !currentPaths.Contains(track.NormalizedPath) && !File.Exists(track.NormalizedPath) && !track.IsMissing)
                { _state.Tracks[index] = track with { IsMissing = true, UpdatedAt = DateTimeOffset.UtcNow }; removed++; }
            }
            var finalStatus = failed > 0 ? LocalScanStatus.PartiallyFailed : LocalScanStatus.Completed;
            _state.Folders = _state.Folders.Select(folder => folder with { LastScanAt = DateTimeOffset.UtcNow, TrackCount = _state.Tracks.Count(track => track.FolderId == folder.Id && !track.IsMissing), ScanStatus = finalStatus }).ToList();
            await SaveAsync(token);
            Publish(new(finalStatus, null, null, processed, files.Count, added, updated, removed, failed, files.Count == 0 ? 1 : 1, "本地音乐索引已更新"));
            return new(finalStatus, added, updated, removed, failed, _state.Tracks.Count(track => !track.IsMissing), "本地音乐索引已更新");
        }
        catch (OperationCanceledException)
        {
            Publish(new(LocalScanStatus.Cancelled, null, null, processed, null, added, updated, removed, failed, null, "本地音乐扫描已取消"));
            return new(LocalScanStatus.Cancelled, added, updated, removed, failed, _state.Tracks.Count(track => !track.IsMissing), "本地音乐扫描已取消");
        }
        finally { _scanCancellation?.Dispose(); _scanCancellation = null; _stateGate.Release(); }
    }

    public async Task<LocalTrack?> GetTrackAsync(string stableId, CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); return _state.Tracks.FirstOrDefault(track => track.Id == stableId); }

    public async Task<IReadOnlyList<LocalTrack>> GetTracksAsync(bool includeMissing = false, CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); return _state.Tracks.Where(track => includeMissing || !track.IsMissing).ToArray(); }

    public async Task<IReadOnlyList<LocalTrack>> GetRecentlyAddedAsync(int limit, CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); return _state.Tracks.Where(track => !track.IsMissing).OrderByDescending(track => track.CreatedAt).Take(Math.Max(0, limit)).ToArray(); }

    public async Task<IReadOnlyList<LocalTrack>> GetRecentlyPlayedAsync(int limit, CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); return _state.Tracks.Where(track => !track.IsMissing && track.LastPlayedAt is not null).OrderByDescending(track => track.LastPlayedAt).Take(Math.Max(0, limit)).ToArray(); }

    public async Task<int> RemoveMissingFilesAsync(CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); var count = _state.Tracks.RemoveAll(track => track.IsMissing); await SaveAsync(cancellationToken); return count; }

    public async Task RecordPlaybackStartedAsync(string stableId, CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); var track = _state.Tracks.FirstOrDefault(item => item.Id == stableId); if (track is not null) { _state.Tracks[_state.Tracks.IndexOf(track)] = track with { LastPlayedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }; await SaveAsync(cancellationToken); } }

    public async Task RecordPlaybackProgressAsync(string stableId, long playedMilliseconds, bool completed, CancellationToken cancellationToken = default)
    { await EnsureLoadedAsync(cancellationToken); var track = _state.Tracks.FirstOrDefault(item => item.Id == stableId); if (track is not null && completed) { _state.Tracks[_state.Tracks.IndexOf(track)] = track with { PlayCount = track.PlayCount + 1, LastPlayedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }; await SaveAsync(cancellationToken); _state.History.Add(new(stableId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, playedMilliseconds, true, true)); } }

    public async Task<IReadOnlyList<LocalCatalogEntry>> SearchAsync(string keyword, SearchResultFilter filter, int offset, int pageSize, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!_state.Folders.Any()) return [];
        var query = keyword.Trim();
        var tracks = _state.Tracks.Where(track => !track.IsMissing && (string.IsNullOrWhiteSpace(query) || Contains(track.Title, query) || Contains(track.Artist, query) || Contains(track.Album, query) || Contains(track.FileName, query))).ToArray();
        if (filter == SearchResultFilter.Tracks) return tracks.OrderBy(track => MatchRank(track, query)).ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase).Skip(Math.Max(0, offset)).Take(Math.Clamp(pageSize, 1, 100)).Select(ToEntry).ToArray();
        if (filter == SearchResultFilter.Playlists) return [];
        var groups = filter == SearchResultFilter.Artists ? tracks.GroupBy(track => track.Artist, StringComparer.OrdinalIgnoreCase).Select(group => ToAggregate(group.First(), SearchResultType.Artist, group.Key)) : tracks.GroupBy(track => track.Album, StringComparer.OrdinalIgnoreCase).Select(group => ToAggregate(group.First(), SearchResultType.Album, group.Key));
        return groups.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).Skip(Math.Max(0, offset)).Take(Math.Clamp(pageSize, 1, 100)).ToArray();
    }

    public Task<IReadOnlyList<SearchSuggestion>> GetSuggestionsAsync(string keyword, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchSuggestion>>([]);

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken) { if (_loaded) return; await _stateGate.WaitAsync(cancellationToken); try { if (!_loaded) { _state = await _persistence.LoadAsync(cancellationToken); _loaded = true; } } finally { _stateGate.Release(); } }
    private Task SaveAsync(CancellationToken cancellationToken) => _persistence.SaveAsync(_state, cancellationToken);
    private void Publish(LocalScanProgress progress) { ScanState = progress; ProgressChanged?.Invoke(this, progress); }
    private static bool Contains(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
    private static int MatchRank(LocalTrack track, string query) => track.Title.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : track.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    private static LocalCatalogEntry ToEntry(LocalTrack track) => new(track.Id, track.Title, track.Artist, track.Album, track.NormalizedPath, track.Duration, track.ArtworkUri, track.Format);
    private static LocalCatalogEntry ToAggregate(LocalTrack track, SearchResultType type, string title) => new($"{type}:{HashId(title)}", title, type == SearchResultType.Artist ? title : track.Artist, type == SearchResultType.Album ? title : track.Album, track.NormalizedPath, track.Duration, track.ArtworkUri, track.Format);
    private static string? NormalizePath(string path) { try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch (Exception) { return null; } }
    private static string StableId(string path) => "local:track:" + HashId(path);
    private static string HashId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()))[..12]).ToLowerInvariant();
    public void Dispose() { _scanCancellation?.Cancel(); _stateGate.Dispose(); }
}
