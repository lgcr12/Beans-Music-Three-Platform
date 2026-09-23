using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.LocalMusic;
using Beans.Windows.Rebuild.Services.Search;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class LocalMusicCatalogTests
{
    [Fact]
    public async Task AddFolderNormalizesAndDeduplicatesWithoutDeletingFiles()
    {
        using var fixture = new LocalFixture();
        var first = await fixture.Catalog.AddFolderAsync(fixture.Root + Path.DirectorySeparatorChar, TestContext.Current.CancellationToken);
        var second = await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await fixture.Catalog.GetFoldersAsync(TestContext.Current.CancellationToken));
        Assert.True(await fixture.Catalog.RemoveFolderAsync(first.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(fixture.AudioPath));
    }

    [Fact]
    public async Task MissingFolderIsRetainedAsUnavailable()
    {
        using var fixture = new LocalFixture();
        var missing = Path.Combine(fixture.Root, "does-not-exist");

        var folder = await fixture.Catalog.AddFolderAsync(missing, TestContext.Current.CancellationToken);

        Assert.False(folder.IsAvailable);
        Assert.False(fixture.Catalog.IsAvailable);
        Assert.Empty(await fixture.Catalog.GetTracksAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScanIndexesSupportedFilesIgnoresOtherExtensionsAndAssociatesLrc()
    {
        using var fixture = new LocalFixture();
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        var summary = await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LocalScanStatus.Completed, summary.Status);
        var tracks = await fixture.Catalog.GetTracksAsync(cancellationToken: TestContext.Current.CancellationToken);
        var track = Assert.Single(tracks);
        Assert.StartsWith("local:track:", track.Id);
        Assert.Equal("Track One", track.Title);
        Assert.Equal("wav", track.FileExtension);
        Assert.Equal(LocalLyricSource.LocalFile, track.LyricSource);
        Assert.NotNull(track.LrcPath);
        Assert.DoesNotContain(tracks, item => item.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RescanningSameFileUpdatesInsteadOfDuplicating()
    {
        using var fixture = new LocalFixture();
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(fixture.AudioPath, DateTime.UtcNow.AddSeconds(2));
        var summary = await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.UpdatedCount);
        Assert.Single(await fixture.Catalog.GetTracksAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletedFileIsMarkedMissingAndExcludedFromSearch()
    {
        using var fixture = new LocalFixture();
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);
        File.Delete(fixture.AudioPath);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Catalog.SearchAsync("Track", SearchResultFilter.Tracks, 0, 20, TestContext.Current.CancellationToken));
        Assert.True((await fixture.Catalog.GetTracksAsync(includeMissing: true, TestContext.Current.CancellationToken)).Single().IsMissing);
    }

    [Fact]
    public async Task SearchSupportsTracksArtistsAlbumsAndStablePaging()
    {
        using var fixture = new LocalFixture(new FixedMetadataReader(), twoTracks: true);
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);

        var tracks = await fixture.Catalog.SearchAsync("Track", SearchResultFilter.Tracks, 0, 1, TestContext.Current.CancellationToken);
        var artists = await fixture.Catalog.SearchAsync("Artist", SearchResultFilter.Artists, 0, 20, TestContext.Current.CancellationToken);
        var albums = await fixture.Catalog.SearchAsync("Album", SearchResultFilter.Albums, 0, 20, TestContext.Current.CancellationToken);

        Assert.Single(tracks);
        Assert.Single(artists);
        Assert.Single(albums);
        Assert.Equal(PlatformId.Local, new LocalMusicSearchAdapter(fixture.Catalog).Platform);
    }

    [Fact]
    public async Task LocalSearchAdapterReturnsCatalogUnavailableBeforeFolderAndLiveAfterScan()
    {
        using var fixture = new LocalFixture();
        var adapter = new LocalMusicSearchAdapter(fixture.Catalog);
        var unavailable = await adapter.SearchAsync(new SearchQuery("Track", Scope: SearchSourceScope.Local), TestContext.Current.CancellationToken);
        Assert.Equal(SearchSourceState.CatalogUnavailable, unavailable.State);

        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);
        var live = await adapter.SearchAsync(new SearchQuery("Track", Scope: SearchSourceScope.Local), TestContext.Current.CancellationToken);
        Assert.Equal(SearchSourceState.Succeeded, live.State);
        Assert.Equal(PlatformId.Local, Assert.Single(live.Items).Platform);
        Assert.True(live.Items[0].IsPlayable);
        Assert.StartsWith("local:track:", live.Items[0].StableId);
    }

    [Fact]
    public async Task ScanCanBeCancelledAndDoesNotLeavePartialTrackWrites()
    {
        using var fixture = new LocalFixture(new DelayingMetadataReader());
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        var summary = await fixture.Catalog.ScanAsync(cancellation.Token);

        Assert.Equal(LocalScanStatus.Cancelled, summary.Status);
        Assert.Empty(await fixture.Catalog.GetTracksAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlaybackSourceValidatesExistingAndMissingFiles()
    {
        using var fixture = new LocalFixture();
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);
        var track = Assert.Single(await fixture.Catalog.GetTracksAsync(cancellationToken: TestContext.Current.CancellationToken));
        var factory = new LocalPlaybackSourceFactory();

        var success = await factory.CreateAsync(track, TestContext.Current.CancellationToken);
        File.Delete(track.NormalizedPath);
        var failure = await factory.CreateAsync(track with { IsMissing = true }, TestContext.Current.CancellationToken);

        Assert.True(success.IsSuccess);
        Assert.False(failure.IsSuccess);
        Assert.Contains("移动或删除", failure.SafeMessage);
    }

    [Fact]
    public async Task PlaybackHistoryUpdatesLocalTrackOnlyOnCompletedProgress()
    {
        using var fixture = new LocalFixture();
        await fixture.Catalog.AddFolderAsync(fixture.Root, TestContext.Current.CancellationToken);
        await fixture.Catalog.ScanAsync(TestContext.Current.CancellationToken);
        var track = Assert.Single(await fixture.Catalog.GetTracksAsync(cancellationToken: TestContext.Current.CancellationToken));

        await fixture.Catalog.RecordPlaybackStartedAsync(track.Id, TestContext.Current.CancellationToken);
        var started = (await fixture.Catalog.GetRecentlyPlayedAsync(5, TestContext.Current.CancellationToken)).Single();
        await fixture.Catalog.RecordPlaybackProgressAsync(track.Id, 1000, completed: false, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await fixture.Catalog.GetTrackAsync(track.Id, TestContext.Current.CancellationToken))!.PlayCount);
        await fixture.Catalog.RecordPlaybackProgressAsync(track.Id, 5000, completed: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, (await fixture.Catalog.GetTrackAsync(track.Id, TestContext.Current.CancellationToken))!.PlayCount);
        Assert.NotNull(started.LastPlayedAt);
    }

    private sealed class LocalFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "beans-local-" + Guid.NewGuid().ToString("N"));
        public string AudioPath => Path.Combine(Root, "Track One.wav");
        public LocalMusicCatalog Catalog { get; }

        public LocalFixture(IAudioMetadataReader? reader = null, bool twoTracks = false)
        {
            Directory.CreateDirectory(Root);
            WriteWave(AudioPath, 1);
            if (twoTracks) File.Copy(AudioPath, Path.Combine(Root, "Track Two.wav"));
            File.WriteAllText(Path.Combine(Root, "Track One.lrc"), "[00:01.00] Track One");
            File.WriteAllText(Path.Combine(Root, "ignore.txt"), "ignore");
            Catalog = new LocalMusicCatalog(reader, new LocalLrcFileResolver(), new LocalMusicCatalogOptions(Path.Combine(Root, "index.json")));
        }

        public void Dispose() { Catalog.Dispose(); try { Directory.Delete(Root, true); } catch { } }

        private static void WriteWave(string path, int seconds)
        {
            const int sampleRate = 8000; const short channels = 1; const short bits = 16;
            var dataSize = sampleRate * seconds * channels * bits / 8;
            using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataSize); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write(channels); writer.Write(sampleRate); writer.Write(sampleRate * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write(bits);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(dataSize); writer.Write(new byte[dataSize]);
        }
    }

    private sealed class DelayingMetadataReader : IAudioMetadataReader
    {
        public async Task<LocalAudioMetadata> ReadAsync(string path, CancellationToken cancellationToken)
        { await Task.Delay(100, cancellationToken); return new(Path.GetFileNameWithoutExtension(path), "Artist", "Album", "", null, null, null, TimeSpan.FromSeconds(1), null, null, "wav", null, LocalMetadataState.Read); }
    }

    private sealed class FixedMetadataReader : IAudioMetadataReader
    {
        public Task<LocalAudioMetadata> ReadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new LocalAudioMetadata(
                Path.GetFileNameWithoutExtension(path), "Artist", "Album", "", null, null, null,
                TimeSpan.FromSeconds(1), null, null, "wav", null, LocalMetadataState.Read));
    }
}
