using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.LocalMusic;

namespace Beans.Windows.Rebuild.Services.MusicUniverse;

public sealed class MusicUniverseMetric(string label, string value, string detail)
{
    public string Label { get; set; } = label;
    public string Value { get; set; } = value;
    public string Detail { get; set; } = detail;
}

public sealed class MusicUniverseArtistInsight(
    string name,
    int trackCount,
    int albumCount,
    int favoriteCount,
    int playCount,
    int sourceCount,
    string sourceSummary)
{
    public string Name { get; set; } = name;
    public int TrackCount { get; set; } = trackCount;
    public int AlbumCount { get; set; } = albumCount;
    public int FavoriteCount { get; set; } = favoriteCount;
    public int PlayCount { get; set; } = playCount;
    public int SourceCount { get; set; } = sourceCount;
    public string SourceSummary { get; set; } = sourceSummary;
    public string TrackCountText => $"{TrackCount} 首歌曲";
    public string AlbumCountText => $"{AlbumCount} 张专辑";
    public string EvidenceText => $"{FavoriteCount} 项收藏 · {PlayCount} 次播放 · {SourceCount} 个来源";
}

public sealed class MusicUniverseAlbumInsight(
    string title,
    string artist,
    int trackCount,
    int favoriteCount,
    int playCount,
    int sourceCount,
    string sourceSummary)
{
    public string Title { get; set; } = title;
    public string Artist { get; set; } = artist;
    public int TrackCount { get; set; } = trackCount;
    public int FavoriteCount { get; set; } = favoriteCount;
    public int PlayCount { get; set; } = playCount;
    public int SourceCount { get; set; } = sourceCount;
    public string SourceSummary { get; set; } = sourceSummary;
    public string TrackCountText => $"{TrackCount} 首歌曲";
    public string EvidenceText => $"{FavoriteCount} 项收藏 · {PlayCount} 次播放 · {SourceCount} 个来源";
}

public sealed class MusicUniverseSourceInsight(
    PlatformId platform,
    string name,
    int itemCount,
    int trackCount,
    int favoriteCount,
    int historyCount,
    int playCount,
    int artistCount,
    int albumCount)
{
    public PlatformId Platform { get; set; } = platform;
    public string Name { get; set; } = name;
    public int ItemCount { get; set; } = itemCount;
    public int TrackCount { get; set; } = trackCount;
    public int FavoriteCount { get; set; } = favoriteCount;
    public int HistoryCount { get; set; } = historyCount;
    public int PlayCount { get; set; } = playCount;
    public int ArtistCount { get; set; } = artistCount;
    public int AlbumCount { get; set; } = albumCount;
    public string ContentText => $"{TrackCount} 首歌曲 · {ArtistCount} 位歌手 · {AlbumCount} 张专辑";
    public string EvidenceText => $"{FavoriteCount} 项收藏 · {HistoryCount} 条播放记录 · {PlayCount} 次播放";
}

public sealed class MusicUniverseRelationship(
    string left,
    string relation,
    string right,
    int evidenceCount,
    string explanation)
{
    public string Left { get; set; } = left;
    public string Relation { get; set; } = relation;
    public string Right { get; set; } = right;
    public int EvidenceCount { get; set; } = evidenceCount;
    public string Explanation { get; set; } = explanation;
    public string Title => $"{Left}  {Relation}  {Right}";
    public string EvidenceText => $"{EvidenceCount} 项真实记录 · {Explanation}";
}

public sealed record MusicUniverseSnapshot(
    IReadOnlyList<MusicUniverseMetric> Metrics,
    IReadOnlyList<MusicUniverseArtistInsight> Artists,
    IReadOnlyList<MusicUniverseAlbumInsight> Albums,
    IReadOnlyList<MusicUniverseSourceInsight> Sources,
    IReadOnlyList<MusicUniverseRelationship> Relationships,
    int IndexedTrackCount,
    int FavoriteCount,
    int HistoryCount,
    int MissingArtistMetadataCount,
    int MissingAlbumMetadataCount,
    LibraryRecoveryStatus RecoveryStatus,
    DateTimeOffset LoadedAt)
{
    public bool HasData => IndexedTrackCount > 0 || FavoriteCount > 0 || HistoryCount > 0;

    public string StatusText
    {
        get
        {
            var parts = new List<string> { $"基于 {IndexedTrackCount} 首本地索引、{FavoriteCount} 项本机收藏和 {HistoryCount} 条播放记录" };
            if (MissingArtistMetadataCount > 0) parts.Add($"{MissingArtistMetadataCount} 首歌曲缺少歌手元数据，未计入歌手关系");
            if (MissingAlbumMetadataCount > 0) parts.Add($"{MissingAlbumMetadataCount} 首歌曲缺少专辑元数据，未计入专辑关系");
            if (RecoveryStatus == LibraryRecoveryStatus.RestoredFromBackup) parts.Add("收藏与历史已从安全备份恢复");
            if (RecoveryStatus == LibraryRecoveryStatus.ResetAfterCorruption) parts.Add("收藏与历史存储损坏后已安全重置");
            return string.Join("；", parts);
        }
    }
}

public interface IMusicUniverseService
{
    Task<MusicUniverseSnapshot> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class MusicUniverseService(
    IUserLibraryService userLibrary,
    ILocalMusicCatalog localMusicCatalog) : IMusicUniverseService
{
    public async Task<MusicUniverseSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var libraryTask = userLibrary.GetSnapshotAsync(cancellationToken);
        var localTask = localMusicCatalog.GetTracksAsync(includeMissing: false, cancellationToken);
        await Task.WhenAll(libraryTask, localTask);
        return MusicUniverseAnalyzer.Build(await libraryTask, await localTask);
    }
}
