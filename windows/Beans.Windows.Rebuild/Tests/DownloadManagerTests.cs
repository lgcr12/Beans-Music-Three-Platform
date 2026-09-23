using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Downloads;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task ProductionResolver_LeavesUnresolvedOnlineTaskExplicitlyUnsupported()
    {
        using var scope = new TempScope();
        using var manager = Manager(scope, new UnsupportedDownloadSourceResolver(), new MemoryTransport([1, 2, 3]));

        var queued = await manager.EnqueueAsync(new DownloadEnqueueRequest(Track("qq", "晴天")), TestContext.Current.CancellationToken);
        Assert.True(queued.IsSuccess);

        var task = await WaitForAsync(manager, item => item.State == DownloadTaskState.Unsupported);
        Assert.Equal(DownloadFailureCode.OfflineDownloadNotAllowed, task.FailureCode);
        Assert.Null(task.FilePath);
        Assert.Contains("尚未提供", task.SafeMessage);
    }

    [Fact]
    public async Task Download_CompletesWithSafeContainedName_AndPersistsWithoutSourceUrl()
    {
        using var scope = new TempScope();
        var content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        var source = new FixedSourceResolver(content.Length);
        using (var manager = Manager(scope, source, new MemoryTransport(content)))
        {
            var queued = await manager.EnqueueAsync(new DownloadEnqueueRequest(Track("safe-id", "../CON:<夜曲>?")), TestContext.Current.CancellationToken);
            Assert.True(queued.IsSuccess);
            var completed = await WaitForAsync(manager, item => item.State == DownloadTaskState.Completed);
            Assert.NotNull(completed.FilePath);
            Assert.True(File.Exists(completed.FilePath));
            Assert.Equal(content, await File.ReadAllBytesAsync(completed.FilePath!, TestContext.Current.CancellationToken));
            Assert.StartsWith(Path.GetFullPath(scope.Downloads) + Path.DirectorySeparatorChar, Path.GetFullPath(completed.FilePath!), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", Path.GetFileName(completed.FilePath));
            Assert.Null(completed.TemporaryFilePath);
        }

        var persisted = await File.ReadAllTextAsync(scope.StatePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("download.music.qq.com", persisted, StringComparison.OrdinalIgnoreCase);
        using var restored = Manager(scope, new UnsupportedDownloadSourceResolver(), new MemoryTransport(content));
        var restoredTask = Assert.Single(await restored.GetTasksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DownloadTaskState.Completed, restoredTask.State);
        Assert.True(File.Exists(restoredTask.FilePath));
    }

    [Fact]
    public async Task InsufficientStorage_IsReportedBeforeTransportStarts()
    {
        using var scope = new TempScope();
        var transport = new MemoryTransport(new byte[2048]);
        using var manager = Manager(scope, new FixedSourceResolver(2048), transport, availableBytes: 1024, reserveBytes: 512);

        await manager.EnqueueAsync(new DownloadEnqueueRequest(Track("disk", "空间测试")), TestContext.Current.CancellationToken);
        var task = await WaitForAsync(manager, item => item.State == DownloadTaskState.InsufficientStorage);

        Assert.Equal(DownloadFailureCode.InsufficientStorage, task.FailureCode);
        Assert.Equal(0, transport.OpenCount);
        Assert.Contains("空间不足", task.SafeMessage);
    }

    [Fact]
    public async Task PauseAndResume_PreservesPartialFileAndUsesRangeOffset()
    {
        using var scope = new TempScope();
        var content = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 239)).ToArray();
        var transport = new MemoryTransport(content, delayPerRead: TimeSpan.FromMilliseconds(3), maximumReadSize: 1024);
        using var manager = Manager(scope, new FixedSourceResolver(content.Length), transport);

        var enqueue = await manager.EnqueueAsync(new DownloadEnqueueRequest(Track("resume", "断点续传")), TestContext.Current.CancellationToken);
        var downloading = await WaitForAsync(manager, item => item.State == DownloadTaskState.Downloading && item.BytesReceived > 0);
        var pausedResult = await manager.PauseAsync(downloading.Id, TestContext.Current.CancellationToken);

        Assert.True(pausedResult.IsSuccess);
        var paused = Assert.Single(await manager.GetTasksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DownloadTaskState.Paused, paused.State);
        Assert.NotNull(paused.TemporaryFilePath);
        Assert.True(File.Exists(paused.TemporaryFilePath));
        Assert.True(new FileInfo(paused.TemporaryFilePath!).Length > 0);

        Assert.True((await manager.ResumeAsync(enqueue.Task!.Id, TestContext.Current.CancellationToken)).IsSuccess);
        var completed = await WaitForAsync(manager, item => item.State == DownloadTaskState.Completed, TimeSpan.FromSeconds(10));
        Assert.Equal(content, await File.ReadAllBytesAsync(completed.FilePath!, TestContext.Current.CancellationToken));
        Assert.Contains(transport.Offsets, offset => offset > 0);
    }

    [Fact]
    public async Task Cancel_CleansPartial_AndRemoveDeletesOnlyRecord()
    {
        using var scope = new TempScope();
        var content = new byte[256 * 1024];
        var transport = new MemoryTransport(content, delayPerRead: TimeSpan.FromMilliseconds(3), maximumReadSize: 1024);
        using var manager = Manager(scope, new FixedSourceResolver(content.Length), transport);

        var enqueue = await manager.EnqueueAsync(new DownloadEnqueueRequest(Track("cancel", "取消测试")), TestContext.Current.CancellationToken);
        var downloading = await WaitForAsync(manager, item => item.State == DownloadTaskState.Downloading && item.BytesReceived > 0);
        var partial = downloading.TemporaryFilePath;
        Assert.True((await manager.CancelAsync(enqueue.Task!.Id, TestContext.Current.CancellationToken)).IsSuccess);

        var cancelled = Assert.Single(await manager.GetTasksAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DownloadTaskState.Cancelled, cancelled.State);
        Assert.False(File.Exists(partial));
        Assert.True((await manager.RemoveAsync(cancelled.Id, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Empty(await manager.GetTasksAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Configuration_RejectsRelativeDirectory_AndPersistsSelectedDirectory()
    {
        using var scope = new TempScope();
        using (var manager = Manager(scope, new UnsupportedDownloadSourceResolver(), new MemoryTransport([])))
        {
            Assert.False((await manager.ConfigureDestinationDirectoryAsync("relative/downloads", TestContext.Current.CancellationToken)).IsSuccess);
            var selected = Path.Combine(scope.Root, "selected");
            Assert.True((await manager.ConfigureDestinationDirectoryAsync(selected, TestContext.Current.CancellationToken)).IsSuccess);
            Assert.Equal(Path.GetFullPath(selected), manager.DestinationDirectory);
        }

        using var restored = Manager(scope, new UnsupportedDownloadSourceResolver(), new MemoryTransport([]));
        await restored.GetTasksAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Path.GetFullPath(Path.Combine(scope.Root, "selected")), restored.DestinationDirectory);
    }

    private static DownloadManager Manager(
        TempScope scope,
        IDownloadSourceResolver resolver,
        IDownloadTransport transport,
        long? availableBytes = 1L << 40,
        long reserveBytes = 0) => new(
            resolver,
            transport,
            new StorageProbe(availableBytes),
            new DownloadManagerOptions(scope.StatePath, scope.Downloads, 2, reserveBytes, 4096, TimeSpan.FromMilliseconds(1)));

    private static MusicTrack Track(string nativeId, string title) => new(
        new MusicIdentity(PlatformId.QqMusic, nativeId),
        title,
        [new MusicArtist(new MusicIdentity(PlatformId.QqMusic, "artist"), "周杰伦", null)],
        null,
        null,
        TimeSpan.FromMinutes(4),
        AvailabilityState.Available,
        AudioQuality.Lossless);

    private static async Task<DownloadTaskSnapshot> WaitForAsync(
        IDownloadManager manager,
        Func<DownloadTaskSnapshot, bool> predicate,
        TimeSpan? timeout = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            var item = (await manager.GetTasksAsync(cancellation.Token)).FirstOrDefault(predicate);
            if (item is not null) return item;
            await Task.Delay(15, cancellation.Token);
        }
    }

    private sealed class FixedSourceResolver(long length) : IDownloadSourceResolver
    {
        public Task<DownloadSourceResult> ResolveAsync(DownloadSourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadSourceResult.Success(new DownloadSource(
                new Uri("https://download.music.qq.com/authorized-audio"), ".flac", "audio/flac", length, true)));
        }
    }

    private sealed class StorageProbe(long? availableBytes) : IDownloadStorageProbe
    {
        public Task<long?> GetAvailableBytesAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(availableBytes);
    }

    private sealed class MemoryTransport(byte[] content, TimeSpan? delayPerRead = null, int maximumReadSize = int.MaxValue) : IDownloadTransport
    {
        public int OpenCount { get; private set; }
        public List<long> Offsets { get; } = [];

        public Task<DownloadTransportResponse> OpenReadAsync(DownloadSource source, long offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            Offsets.Add(offset);
            var stream = new DelayedMemoryStream(content, checked((int)offset), delayPerRead ?? TimeSpan.Zero, maximumReadSize);
            return Task.FromResult(new DownloadTransportResponse(stream, content.LongLength, offset > 0, "audio/flac"));
        }
    }

    private sealed class DelayedMemoryStream : MemoryStream
    {
        private readonly TimeSpan _delay;
        private readonly int _maximumReadSize;

        public DelayedMemoryStream(byte[] bytes, int offset, TimeSpan delay, int maximumReadSize) : base(bytes, offset, bytes.Length - offset, false)
        {
            _delay = delay;
            _maximumReadSize = maximumReadSize;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, _maximumReadSize)], cancellationToken);
        }
    }

    private sealed class TempScope : IDisposable
    {
        public TempScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "beans-download-tests", Guid.NewGuid().ToString("N"));
            Downloads = Path.Combine(Root, "downloads");
            StatePath = Path.Combine(Root, "state", "downloads.json");
            Directory.CreateDirectory(Downloads);
        }

        public string Root { get; }
        public string Downloads { get; }
        public string StatePath { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
