using Beans.Windows.Rebuild.Services.LocalMusic;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class LocalMusicGroupingTests
{
    [Fact]
    public void AlbumsUseAlbumAndContributorWithoutMergingDifferentArtists()
    {
        var groups = LocalMusicGrouping.ByAlbum([
            Track("one", "First", "Shared Album", "Artist A", "Artist A", trackNumber: 2),
            Track("two", "Second", "shared album", "Artist A", "artist a", trackNumber: 1),
            Track("three", "Third", "Shared Album", "Artist B", "Artist B")
        ]);

        Assert.Equal(2, groups.Count);
        var artistA = Assert.Single(groups, group => group.SecondaryText == "Artist A");
        Assert.Equal(["Second", "First"], artistA.Tracks.Select(track => track.Title));
        Assert.Equal("2 首", artistA.TrackCountText);
    }

    [Fact]
    public void ArtistsExposeMissingFilesAndUseUnknownMetadataFallbacks()
    {
        var groups = LocalMusicGrouping.ByArtist([
            Track("one", "Available", "", "", "", isMissing: false),
            Track("two", "Missing", "", "", "", isMissing: true)
        ]);

        var unknown = Assert.Single(groups);
        Assert.Equal("未知艺术家", unknown.Name);
        Assert.Equal(1, unknown.AvailableTrackCount);
        Assert.Equal(1, unknown.MissingTrackCount);
        Assert.Equal("1 首可用 · 1 首文件缺失", unknown.TrackCountText);

        var unknownAlbum = Assert.Single(LocalMusicGrouping.ByAlbum(unknown.Tracks));
        Assert.Equal("未知专辑", unknownAlbum.Name);
        Assert.Equal("未知艺术家", unknownAlbum.SecondaryText);
    }

    [Fact]
    public void ArtistGroupsReportDistinctAlbumCountCaseInsensitively()
    {
        var group = Assert.Single(LocalMusicGrouping.ByArtist([
            Track("one", "One", "Album", "Artist", ""),
            Track("two", "Two", "album", "artist", ""),
            Track("three", "Three", "Another", "Artist", "")
        ]));

        Assert.Equal("2 张专辑", group.SecondaryText);
        Assert.Equal(3, group.AvailableTrackCount);
    }

    private static LocalTrack Track(
        string id,
        string title,
        string album,
        string artist,
        string albumArtist,
        int? trackNumber = null,
        bool isMissing = false) => new(
            Id: $"local:track:{id}",
            FolderId: "folder",
            NormalizedPath: $"C:\\Music\\{id}.flac",
            DisplayPath: $"C:\\Music\\{id}.flac",
            FileName: $"{id}.flac",
            FileExtension: "flac",
            FileSize: 1,
            LastWriteTimeUtc: DateTime.UtcNow,
            Title: title,
            Artist: artist,
            Album: album,
            AlbumArtist: albumArtist,
            TrackNumber: trackNumber,
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
            IsMissing: isMissing,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastPlayedAt: null,
            PlayCount: 0);
}
