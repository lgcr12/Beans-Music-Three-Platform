using Beans.Windows.Rebuild.Services.BeansPlaylists;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class BeansPlaylistPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string PathForTest() => Path.Combine(AppContext.BaseDirectory, "artifacts", "playlist-persistence", Guid.NewGuid().ToString("N"), "playlists.json");

    [Fact]
    public async Task ConcurrentServiceInstancesDoNotLosePlaylists()
    {
        var path = PathForTest();
        var services = Enumerable.Range(0, 12).Select(_ => new JsonBeansPlaylistService(path)).ToArray();
        await Task.WhenAll(services.Select((service, index) => service.CreateAsync("Playlist " + index, Ct)));
        var saved = await new JsonBeansPlaylistService(path).GetPlaylistsAsync(Ct);
        Assert.Equal(12, saved.Count);
        Assert.Equal(12, saved.Select(s => s.Title).Distinct().Count());
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task CorruptFileWithoutBackupIsNotEmptyAndCannotBeOverwritten()
    {
        var path = PathForTest();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{broken", Ct);
        var service = new JsonBeansPlaylistService(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetPlaylistsAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync("New", Ct));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task BackupRecoversPlaylistAndPreservesCorruptOriginalDuringNextSave()
    {
        var path = PathForTest();
        var service = new JsonBeansPlaylistService(path);
        var saved = await service.CreateAsync("Original", Ct);
        await File.WriteAllTextAsync(path, "{broken", Ct);
        Assert.Equal(saved.Id, Assert.Single(await service.GetPlaylistsAsync(Ct)).Id);
        await service.CreateAsync("New", Ct);
        Assert.Equal(2, (await new JsonBeansPlaylistService(path).GetPlaylistsAsync(Ct)).Count);
        Assert.Equal("{broken", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*")), Ct));
        Assert.Contains("Original", await File.ReadAllTextAsync(path + ".bak", Ct));
    }

    [Fact]
    public async Task LockedDataFileDoesNotAppearEmptyOrGetReplaced()
    {
        var path = PathForTest();
        var service = new JsonBeansPlaylistService(path);
        await service.CreateAsync("Original", Ct);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.GetPlaylistsAsync(Ct));
            await Assert.ThrowsAsync<IOException>(() => service.CreateAsync("New", Ct));
        }
        Assert.Equal("Original", Assert.Single(await service.GetPlaylistsAsync(Ct)).Title);
    }

    [Fact]
    public async Task WaitingForOtherInstanceCanBeCancelledWithoutChangingData()
    {
        var path = PathForTest();
        var service = new JsonBeansPlaylistService(path);
        await service.CreateAsync("Original", Ct);
        using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var cancellation = new CancellationTokenSource())
        {
            var create = service.CreateAsync("New", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        }
        Assert.Single(await service.GetPlaylistsAsync(Ct));
    }

    [Fact]
    public async Task HeldWriterLockDoesNotHideLastCommittedPlaylistsFromReaders()
    {
        var path = PathForTest();
        var service = new JsonBeansPlaylistService(path);
        await service.CreateAsync("Original", Ct);
        using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var saved = await new JsonBeansPlaylistService(path).GetPlaylistsAsync(Ct);
            Assert.Equal("Original", Assert.Single(saved).Title);
        }
    }

    [Fact]
    public async Task MissingPrimaryRecoversFromBackupInsteadOfStartingEmpty()
    {
        var path = PathForTest();
        var service = new JsonBeansPlaylistService(path);
        await service.CreateAsync("Original", Ct);
        File.Delete(path);
        Assert.Equal("Original", Assert.Single(await service.GetPlaylistsAsync(Ct)).Title);
        await service.CreateAsync("New", Ct);
        Assert.Equal(2, (await service.GetPlaylistsAsync(Ct)).Count);
    }
}
