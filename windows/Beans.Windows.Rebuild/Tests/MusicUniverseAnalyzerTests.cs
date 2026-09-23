using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.MusicUniverse;
using Xunit;
using LocalIndexedTrack = Beans.Windows.Rebuild.Services.LocalMusic.LocalTrack;

namespace Beans.Windows.Rebuild.Tests;

public sealed class MusicUniverseAnalyzerTests
{
    [Fact]
    public void EmptyInputsProduceExplicitEmptySnapshotWithoutSyntheticNodes()
    {
        var result = MusicUniverseAnalyzer.Build(Library(), []);

        Assert.False(result.HasData);
        Assert.Empty(result.Artists);
        Assert.Empty(result.Albums);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Relationships);
        Assert.All(result.Metrics, metric => Assert.DoesNotContain("示例", metric.Detail));
    }

    [Fact]
    public void LocalIndexBuildsArtistAlbumSourceAndExplainableRelationships()
    {
        var result = MusicUniverseAnalyzer.Build(
            Library(),
            [
                Track("one", "First", "Artist A", "Album A", playCount: 3),
                Track("two", "Second", "Artist A", "Album A", playCount: 1)
            ]);

        Assert.True(result.HasData);
        var artist = Assert.Single(result.Artists);
        Assert.Equal("Artist A", artist.Name);
        Assert.Equal(2, artist.TrackCount);
        Assert.Equal(1, artist.AlbumCount);
        Assert.Equal(4, artist.PlayCount);

        var album = Assert.Single(result.Albums);
        Assert.Equal("Album A", album.Title);
        Assert.Equal(2, album.TrackCount);

        var source = Assert.Single(result.Sources);
        Assert.Equal(PlatformId.Local, source.Platform);
        Assert.Equal(2, source.TrackCount);
        Assert.Contains(result.Relationships, relation =>
            relation.Left == "Artist A" && relation.Relation == "收录于" && relation.Right == "Album A" && relation.EvidenceCount == 2);
    }

    [Fact]
    public void SameIdentityAcrossIndexFavoriteAndHistoryIsMergedWithoutDoubleCountingPlays()
    {
        var item = Snapshot(PlatformId.Local, "local:track:one", "First", "Artist", "Album");
        var result = MusicUniverseAnalyzer.Build(
            Library(
                favorites: [new FavoriteEntry(item, DateTimeOffset.Parse("2026-01-02T00:00:00Z"))],
                history: [new PlaybackHistoryEntry(item, DateTimeOffset.Parse("2026-01-03T00:00:00Z"), 1000, 5)]),
            [Track("one", "First", "Artist", "Album", playCount: 4)]);

        Assert.Equal("1 首", result.Metrics.Single(metric => metric.Label == "真实歌曲").Value);
        var artist = Assert.Single(result.Artists);
        Assert.Equal(1, artist.TrackCount);
        Assert.Equal(1, artist.FavoriteCount);
        Assert.Equal(5, artist.PlayCount);
        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.ItemCount);
        Assert.Equal(1, source.HistoryCount);
    }

    [Fact]
    public void PreviewLibraryItemsNeverBecomeUniverseEvidence()
    {
        var preview = Snapshot(PlatformId.QqMusic, "preview", "Preview", "Fake Artist", "Fake Album") with
        {
            DataOrigin = SearchDataOrigin.Preview
        };
        var result = MusicUniverseAnalyzer.Build(
            Library(favorites: [new FavoriteEntry(preview, DateTimeOffset.UtcNow)]),
            []);

        Assert.True(result.HasData);
        Assert.Empty(result.Artists);
        Assert.Empty(result.Albums);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void MissingMetadataIsReportedAndDoesNotCreateFakeArtistOrAlbum()
    {
        var result = MusicUniverseAnalyzer.Build(Library(), [Track("one", "Untitled", "", "")]);

        Assert.True(result.HasData);
        Assert.Equal(1, result.MissingArtistMetadataCount);
        Assert.Equal(1, result.MissingAlbumMetadataCount);
        Assert.Empty(result.Artists);
        Assert.Empty(result.Albums);
        Assert.Contains("未计入歌手关系", result.StatusText);
        Assert.Contains("未计入专辑关系", result.StatusText);
    }

    [Fact]
    public void CrossPlatformItemsRemainSeparateAndExposeTheirRealSources()
    {
        var qq = Snapshot(PlatformId.QqMusic, "42", "Song", "Shared Artist", "Shared Album");
        var netease = Snapshot(PlatformId.NetEaseMusic, "42", "Song", "Shared Artist", "Shared Album");
        var result = MusicUniverseAnalyzer.Build(
            Library(
                favorites: [new FavoriteEntry(qq, DateTimeOffset.UtcNow), new FavoriteEntry(netease, DateTimeOffset.UtcNow)],
                history: [new PlaybackHistoryEntry(qq, DateTimeOffset.UtcNow, 1000, 2)]),
            []);

        Assert.Equal(2, result.Sources.Count);
        Assert.Contains(result.Sources, source => source.Platform == PlatformId.QqMusic);
        Assert.Contains(result.Sources, source => source.Platform == PlatformId.NetEaseMusic);
        var artist = Assert.Single(result.Artists);
        Assert.Equal(2, artist.TrackCount);
        Assert.Equal(2, artist.SourceCount);
        Assert.Contains("QQ 音乐", artist.SourceSummary);
        Assert.Contains("网易云音乐", artist.SourceSummary);
    }

    private static UserLibrarySnapshot Library(
        IReadOnlyList<FavoriteEntry>? favorites = null,
        IReadOnlyList<PlaybackHistoryEntry>? history = null) =>
        new(favorites ?? [], history ?? [], LibraryRecoveryStatus.Normal, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static LibraryMediaSnapshot Snapshot(
        PlatformId platform,
        string nativeId,
        string title,
        string artist,
        string album) =>
        new(LibraryItemKind.Track, platform, nativeId, title, artist, album, DataOrigin: SearchDataOrigin.Live);

    private static LocalIndexedTrack Track(
        string id,
        string title,
        string artist,
        string album,
        int playCount = 0) => new(
        Id: $"local:track:{id}",
        FolderId: "folder",
        NormalizedPath: $"D:\\Music\\{id}.flac",
        DisplayPath: $"D:\\Music\\{id}.flac",
        FileName: $"{id}.flac",
        FileExtension: "flac",
        FileSize: 100,
        LastWriteTimeUtc: DateTime.UtcNow,
        Title: title,
        Artist: artist,
        Album: album,
        AlbumArtist: artist,
        TrackNumber: 1,
        DiscNumber: 1,
        Year: 2026,
        Duration: TimeSpan.FromMinutes(3),
        Bitrate: 900,
        SampleRate: 44100,
        Format: "FLAC",
        ArtworkUri: "ms-appx:///Assets/Branding/beans-icon.png",
        LrcPath: null,
        LyricSource: LocalLyricSource.None,
        MetadataState: LocalMetadataState.Read,
        IsMissing: false,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        LastPlayedAt: playCount > 0 ? DateTimeOffset.UtcNow : null,
        PlayCount: playCount);
}
