using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class UserLibraryServiceTests
{
    [Fact]
    public async Task FavoriteRoundTripPreservesProviderIdentityAndNeverPersistsPlaybackSecrets()
    {
        using var fixture = new LibraryFixture();
        var searchItem = new SearchResultItem(
            SearchResultType.Track,
            PlatformId.QqMusic,
            "0039MnYb0qxYhV",
            "qq:track:0039MnYb0qxYhV",
            "真实歌曲",
            Artist: "歌手",
            Album: "专辑",
            DataOrigin: SearchDataOrigin.Live,
            PayloadReference: "provider-payload-secret",
            PlaybackUri: "https://media.example/audio.m4a?token=super-secret");

        Assert.True(LibraryMediaSnapshot.TryCreate(searchItem, out var snapshot));
        Assert.True(await fixture.Service.SetFavoriteAsync(snapshot!, true, TestContext.Current.CancellationToken));
        var stored = (await fixture.Service.GetSnapshotAsync(TestContext.Current.CancellationToken)).Favorites.Single();
        var json = await File.ReadAllTextAsync(fixture.StoragePath, TestContext.Current.CancellationToken);

        Assert.Equal(PlatformId.QqMusic, stored.Item.Platform);
        Assert.Equal("0039MnYb0qxYhV", stored.Item.NativeId);
        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-payload-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("playbackUri", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.StoragePath + ".tmp"));
    }

    [Fact]
    public async Task FavoritesAreIdentityDeduplicatedAndCanBeRemoved()
    {
        using var fixture = new LibraryFixture();
        var first = Track(PlatformId.NetEaseMusic, "123", "旧标题");
        var updated = first with { Title = "新标题" };

        Assert.True(await fixture.Service.SetFavoriteAsync(first, true, TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.SetFavoriteAsync(updated, true, TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.IsFavoriteAsync(LibraryItemKind.Track, PlatformId.NetEaseMusic, "123", TestContext.Current.CancellationToken));
        Assert.Equal("新标题", (await fixture.Service.GetSnapshotAsync(TestContext.Current.CancellationToken)).Favorites.Single().Title);
        Assert.True(await fixture.Service.SetFavoriteAsync(updated, false, TestContext.Current.CancellationToken));
        Assert.Empty((await fixture.Service.GetSnapshotAsync(TestContext.Current.CancellationToken)).Favorites);
        Assert.False(await fixture.Service.SetFavoriteAsync(updated, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreviewItemsCannotBecomeRealFavoritesOrHistory()
    {
        using var fixture = new LibraryFixture();
        var previewSearch = new SearchResultItem(
            SearchResultType.Track, PlatformId.QqMusic, "preview", "preview", "预览歌曲", DataOrigin: SearchDataOrigin.Preview);
        var preview = Track(PlatformId.QqMusic, "preview", "预览歌曲") with { DataOrigin = SearchDataOrigin.Preview };

        Assert.False(LibraryMediaSnapshot.TryCreate(previewSearch, out _));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SetFavoriteAsync(preview, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RecordPlaybackAsync(preview, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlaybackHistoryUsesMostRecentIdentityDeduplicationAndBoundedRetention()
    {
        using var fixture = new LibraryFixture(maximumHistory: 2);
        var a = Track(PlatformId.QqMusic, "A1", "A");
        var b = Track(PlatformId.NetEaseMusic, "2", "B");
        var c = Track(PlatformId.QqMusic, "C3", "C");

        await fixture.Service.RecordPlaybackAsync(a, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.RecordPlaybackAsync(b, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.RecordPlaybackAsync(a with { Title = "A 已更新" }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var deduplicated = (await fixture.Service.GetSnapshotAsync(TestContext.Current.CancellationToken)).RecentHistory;
        Assert.Equal(2, deduplicated.Count);
        Assert.Equal("A 已更新", deduplicated[0].Title);
        Assert.Equal(2, deduplicated[0].PlayCount);
        Assert.Equal(3000, deduplicated[0].LastPlayedDurationMilliseconds);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.RecordPlaybackAsync(c, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var bounded = (await fixture.Service.GetSnapshotAsync(TestContext.Current.CancellationToken)).RecentHistory;
        Assert.Equal(["C3", "A1"], bounded.Select(item => item.Item.NativeId));
    }

    [Fact]
    public async Task CorruptPrimaryFileRestoresLastKnownGoodBackup()
    {
        using var fixture = new LibraryFixture();
        await fixture.Service.SetFavoriteAsync(Track(PlatformId.QqMusic, "A1", "A"), true, TestContext.Current.CancellationToken);
        await fixture.Service.SetFavoriteAsync(Track(PlatformId.NetEaseMusic, "2", "B"), true, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixture.StoragePath, "{ truncated", TestContext.Current.CancellationToken);

        using var restoredService = new UserLibraryService(new UserLibraryOptions(fixture.StoragePath));
        var restored = await restoredService.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryRecoveryStatus.RestoredFromBackup, restored.RecoveryStatus);
        Assert.Single(restored.Favorites);
        Assert.Equal("A1", restored.Favorites[0].Item.NativeId);
    }

    [Fact]
    public async Task CorruptPrimaryAndBackupResetSafelyWithoutLeakingParserErrors()
    {
        using var fixture = new LibraryFixture();
        await fixture.Service.SetFavoriteAsync(Track(PlatformId.QqMusic, "A1", "A"), true, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixture.StoragePath, "not-json", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixture.StoragePath + ".bak", "also-not-json", TestContext.Current.CancellationToken);

        using var resetService = new UserLibraryService(new UserLibraryOptions(fixture.StoragePath));
        var reset = await resetService.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryRecoveryStatus.ResetAfterCorruption, reset.RecoveryStatus);
        Assert.Empty(reset.Favorites);
        Assert.Empty(reset.RecentHistory);
    }

    [Fact]
    public async Task StructurallyValidButInvalidEntriesAreIgnoredDuringSafeRestore()
    {
        using var fixture = new LibraryFixture();
        await File.WriteAllTextAsync(fixture.StoragePath,
            """{"schemaVersion":1,"favorites":[{"item":null,"addedAt":"2026-09-21T00:00:00Z"}],"history":[{"item":null,"lastPlayedAt":"2026-09-21T00:00:00Z","lastPlayedDurationMilliseconds":0,"playCount":1}]}""",
            TestContext.Current.CancellationToken);

        var snapshot = await fixture.Service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryRecoveryStatus.Normal, snapshot.RecoveryStatus);
        Assert.Empty(snapshot.Favorites);
        Assert.Empty(snapshot.RecentHistory);
    }

    private static LibraryMediaSnapshot Track(PlatformId platform, string nativeId, string title) =>
        new(LibraryItemKind.Track, platform, nativeId, title, "歌手", "专辑", "https://images.example/cover.jpg", 180_000, "HQ", SearchDataOrigin.Live);

    private sealed class LibraryFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "beans-library-" + Guid.NewGuid().ToString("N"));
        public string StoragePath => Path.Combine(_root, "user-library.json");
        public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        public UserLibraryService Service { get; }

        public LibraryFixture(int maximumHistory = 200)
        {
            Directory.CreateDirectory(_root);
            Service = new UserLibraryService(new UserLibraryOptions(StoragePath, MaximumHistoryEntries: maximumHistory), Clock);
        }

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
