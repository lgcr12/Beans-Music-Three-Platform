using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.LocalMusic;

public enum LocalScanStatus { Idle, SelectingFolder, Scanning, Cancelling, Completed, Cancelled, PartiallyFailed, Failed }
public enum LocalMetadataState { Read, Fallback, Failed }
public enum LocalLyricSource { None, LocalFile }

public sealed record LocalMusicFolder(
    string Id,
    string Path,
    string DisplayName,
    DateTimeOffset AddedAt,
    DateTimeOffset? LastScanAt,
    bool IsAvailable,
    int TrackCount,
    LocalScanStatus ScanStatus);

public sealed record LocalTrack(
    string Id,
    string FolderId,
    string NormalizedPath,
    string DisplayPath,
    string FileName,
    string FileExtension,
    long FileSize,
    DateTime LastWriteTimeUtc,
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    TimeSpan Duration,
    int? Bitrate,
    int? SampleRate,
    string Format,
    string ArtworkUri,
    string? LrcPath,
    LocalLyricSource LyricSource,
    LocalMetadataState MetadataState,
    bool IsMissing,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastPlayedAt,
    int PlayCount)
{
    public string DurationText => Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"mm\:ss");
    public string SampleRateText => SampleRate is { } rate ? $"{rate / 1000d:0.#} kHz" : "采样率未知";
    public string LyricStatusText => LyricSource == LocalLyricSource.LocalFile ? "本地 LRC" : "无歌词";
    public string AvailabilityText => IsMissing ? "文件已移动或删除" : "本地音乐";
}

public sealed record LocalScanProgress(
    LocalScanStatus Status,
    string? CurrentFolder,
    string? CurrentFile,
    int ProcessedFiles,
    int? TotalFiles,
    int AddedCount,
    int UpdatedCount,
    int RemovedCount,
    int FailedCount,
    double? ProgressRatio,
    string SafeMessage);

public sealed record LocalScanSummary(
    LocalScanStatus Status,
    int AddedCount,
    int UpdatedCount,
    int RemovedCount,
    int FailedCount,
    int IndexedTrackCount,
    string SafeMessage);

public sealed record LocalAudioMetadata(
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    TimeSpan Duration,
    int? Bitrate,
    int? SampleRate,
    string Format,
    string? EmbeddedArtworkPath,
    LocalMetadataState State);

public sealed record LocalPlaybackSource(bool IsSuccess, string? SourceUri, string SafeMessage)
{
    public static LocalPlaybackSource Success(string sourceUri) => new(true, sourceUri, "本地播放源已准备");
    public static LocalPlaybackSource Failure(string message) => new(false, null, message);
}

public sealed record LocalPlayHistoryEntry(
    string LocalTrackId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long PlayedMilliseconds,
    bool IsCompleted,
    bool PlayCountIncrement);

public sealed record LocalMusicCatalogOptions(string? StoragePath = null, long MaximumFileBytes = 512L * 1024 * 1024)
{
    public string EffectiveStoragePath => StoragePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeansMusic", "Rebuild", "local-index.json");
}

public interface IAudioMetadataReader
{
    Task<LocalAudioMetadata> ReadAsync(string path, CancellationToken cancellationToken);
}

public interface ILrcFileResolver
{
    Task<(string? Path, LocalLyricSource Source)> ResolveAsync(string audioPath, CancellationToken cancellationToken);
}

public interface ILocalMusicCatalog : ILocalMusicSearchCatalog
{
    event EventHandler<LocalScanProgress>? ProgressChanged;
    LocalScanProgress ScanState { get; }
    Task<LocalMusicFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> RemoveFolderAsync(string pathOrId, bool removeIndex = true, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalMusicFolder>> GetFoldersAsync(CancellationToken cancellationToken = default);
    Task<LocalScanSummary> ScanAsync(CancellationToken cancellationToken = default);
    Task<LocalTrack?> GetTrackAsync(string stableId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalTrack>> GetTracksAsync(bool includeMissing = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalTrack>> GetRecentlyAddedAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalTrack>> GetRecentlyPlayedAsync(int limit, CancellationToken cancellationToken = default);
    Task<int> RemoveMissingFilesAsync(CancellationToken cancellationToken = default);
    Task RecordPlaybackStartedAsync(string stableId, CancellationToken cancellationToken = default);
    Task RecordPlaybackProgressAsync(string stableId, long playedMilliseconds, bool completed, CancellationToken cancellationToken = default);
}
