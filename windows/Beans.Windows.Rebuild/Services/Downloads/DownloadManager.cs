using System.Collections.Concurrent;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Downloads;

public sealed class DownloadManager : IDownloadManager
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma" };
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    private readonly IDownloadSourceResolver _sourceResolver;
    private readonly IDownloadTransport _transport;
    private readonly IDownloadStorageProbe _storageProbe;
    private readonly DownloadManagerOptions _options;
    private readonly DownloadPersistence _persistence;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _workerGate;
    private readonly ConcurrentDictionary<string, TaskRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DownloadTaskSnapshot> _tasks = new(StringComparer.Ordinal);
    private string _destinationDirectory;
    private bool _loaded;
    private bool _disposed;

    public DownloadManager(
        IDownloadSourceResolver sourceResolver,
        IDownloadTransport transport,
        IDownloadStorageProbe storageProbe,
        DownloadManagerOptions? options = null)
    {
        _sourceResolver = sourceResolver;
        _transport = transport;
        _storageProbe = storageProbe;
        _options = options ?? new DownloadManagerOptions();
        _persistence = new DownloadPersistence(_options.EffectiveStoragePath);
        _destinationDirectory = _options.EffectiveDefaultDestinationDirectory;
        _workerGate = new SemaphoreSlim(Math.Clamp(_options.MaximumConcurrentDownloads, 1, 8));
    }

    public event EventHandler? TasksChanged;
    public string DestinationDirectory => _destinationDirectory;

    public async Task<IReadOnlyList<DownloadTaskSnapshot>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _stateGate.WaitAsync(cancellationToken);
        try { return _tasks.Values.OrderByDescending(task => task.CreatedAt).ToArray(); }
        finally { _stateGate.Release(); }
    }

    public async Task<DownloadOperationResult> ConfigureDestinationDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var normalized = NormalizeDirectory(directory);
        if (normalized is null) return DownloadOperationResult.Failure("下载目录无效，请重新选择");
        try { Directory.CreateDirectory(normalized); }
        catch (UnauthorizedAccessException) { return DownloadOperationResult.Failure("没有权限写入该下载目录"); }
        catch (IOException) { return DownloadOperationResult.Failure("下载目录暂时不可用"); }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            _destinationDirectory = normalized;
            await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }
        RaiseTasksChanged();
        return new DownloadOperationResult(true, null, "下载目录已更新");
    }

    public async Task<DownloadOperationResult> EnqueueAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Track);
        await EnsureLoadedAsync(cancellationToken);

        if (request.Track.Identity.Platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic))
            return DownloadOperationResult.Failure("该来源不支持在线离线下载");
        if (string.IsNullOrWhiteSpace(request.Track.Identity.NativeId) || string.IsNullOrWhiteSpace(request.Track.Title))
            return DownloadOperationResult.Failure("曲目信息不完整，无法创建下载任务");

        var destination = NormalizeDirectory(request.DestinationDirectory ?? _destinationDirectory);
        if (destination is null) return DownloadOperationResult.Failure("请先选择有效的下载目录");
        try { Directory.CreateDirectory(destination); }
        catch (UnauthorizedAccessException) { return DownloadOperationResult.Failure("没有权限写入该下载目录"); }
        catch (IOException) { return DownloadOperationResult.Failure("下载目录暂时不可用"); }

        DownloadTaskSnapshot task;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var duplicate = _tasks.Values.FirstOrDefault(item =>
                item.Identity == request.Track.Identity &&
                item.State is DownloadTaskState.Queued or DownloadTaskState.Resolving or DownloadTaskState.Downloading or DownloadTaskState.Paused);
            if (duplicate is not null) return DownloadOperationResult.Success(duplicate, "该曲目已有下载任务");

            var now = DateTimeOffset.UtcNow;
            var artist = request.Track.Artists.FirstOrDefault()?.Name ?? "未知艺术家";
            task = new DownloadTaskSnapshot(
                Guid.NewGuid().ToString("N"), request.Track.Identity, request.Track.Title.Trim(), artist.Trim(),
                request.Track.Cover?.AbsoluteUri ?? string.Empty, request.RequestedQuality, destination,
                null, null, 0, null, DownloadTaskState.Queued, DownloadFailureCode.None,
                "等待检查平台离线下载授权", now, now);
            _tasks.Add(task.Id, task);
            await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }

        RaiseTasksChanged();
        StartProcessing(task.Id);
        return DownloadOperationResult.Success(task, "下载任务已创建");
    }

    public async Task<DownloadOperationResult> PauseAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await SetControlStateAsync(taskId,
            task => task.CanPause,
            DownloadTaskState.Paused,
            DownloadFailureCode.None,
            "下载已暂停",
            cancellationToken);
        if (!result.IsSuccess) return result;
        await StopRuntimeAsync(taskId);
        return result with { Task = await GetTaskAsync(taskId, cancellationToken) };
    }

    public async Task<DownloadOperationResult> ResumeAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await SetControlStateAsync(taskId,
            task => task.CanResume,
            DownloadTaskState.Queued,
            DownloadFailureCode.None,
            "等待继续下载",
            cancellationToken);
        if (!result.IsSuccess) return result;
        StartProcessing(taskId);
        return result;
    }

    public async Task<DownloadOperationResult> CancelAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var result = await SetControlStateAsync(taskId,
            task => task.CanCancel,
            DownloadTaskState.Cancelled,
            DownloadFailureCode.Cancelled,
            "下载已取消",
            cancellationToken);
        if (!result.IsSuccess) return result;
        await StopRuntimeAsync(taskId);
        var current = await GetTaskAsync(taskId, cancellationToken);
        if (current is not null) TryDeletePartial(current);
        return result with { Task = current };
    }

    public async Task<DownloadOperationResult> RemoveAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        DownloadTaskSnapshot? removed;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_tasks.TryGetValue(taskId, out removed)) return DownloadOperationResult.Failure("下载任务不存在");
            if (!removed.CanRemove) return DownloadOperationResult.Failure("请先暂停或取消正在进行的任务");
            _tasks.Remove(taskId);
            await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }
        TryDeletePartial(removed);
        RaiseTasksChanged();
        return new DownloadOperationResult(true, removed, "下载记录已移除，已完成的音乐文件会保留");
    }

    private void StartProcessing(string taskId)
    {
        if (_disposed || _runtimes.ContainsKey(taskId)) return;
        var cancellation = new CancellationTokenSource();
        var runtime = new TaskRuntime(cancellation);
        if (!_runtimes.TryAdd(taskId, runtime)) { cancellation.Dispose(); return; }
        runtime.Task = Task.Run(() => ProcessAsync(taskId, cancellation.Token));
    }

    private async Task ProcessAsync(string taskId, CancellationToken cancellationToken)
    {
        var acquiredWorker = false;
        try
        {
            await _workerGate.WaitAsync(cancellationToken);
            acquiredWorker = true;
            var task = await TransitionAsync(taskId, DownloadTaskState.Resolving, DownloadFailureCode.None, "正在检查平台授权与离线下载能力", cancellationToken);
            if (task is null) return;

            var sourceResult = await _sourceResolver.ResolveAsync(new DownloadSourceRequest(task.Identity, task.RequestedQuality), cancellationToken);
            if (!sourceResult.IsSuccess || sourceResult.Source is null)
            {
                var state = sourceResult.FailureCode switch
                {
                    DownloadFailureCode.AuthorizationRequired => DownloadTaskState.AuthorizationRequired,
                    DownloadFailureCode.OfflineDownloadNotAllowed => DownloadTaskState.Unsupported,
                    _ => DownloadTaskState.Failed
                };
                await TransitionAsync(taskId, state, sourceResult.FailureCode, sourceResult.SafeMessage, CancellationToken.None);
                return;
            }

            var source = sourceResult.Source;
            ValidateSource(source);
            task = await PreparePathsAsync(taskId, source, cancellationToken);
            if (task is null) return;
            var offset = GetPartialLength(task);
            if (offset > 0 && !source.SupportsRangeRequests)
            {
                TruncatePartial(task);
                offset = 0;
            }
            var expectedTotal = source.TotalBytes;
            if (!await HasStorageAsync(task.DestinationDirectory, expectedTotal is { } total ? Math.Max(0, total - offset) : 0, cancellationToken))
            {
                await TransitionAsync(taskId, DownloadTaskState.InsufficientStorage, DownloadFailureCode.InsufficientStorage, "可用磁盘空间不足，下载未开始", CancellationToken.None);
                return;
            }

            var response = await _transport.OpenReadAsync(source, offset, cancellationToken);
            if (offset > 0 && !response.IsPartialContent)
            {
                await response.DisposeAsync();
                TruncatePartial(task);
                offset = 0;
                response = await _transport.OpenReadAsync(source, 0, cancellationToken);
            }
            await using (response)
            {
                ValidateResponse(response, source);
                expectedTotal = response.TotalBytes ?? source.TotalBytes;
                if (!await HasStorageAsync(task.DestinationDirectory, expectedTotal is { } expectedBytes ? Math.Max(0, expectedBytes - offset) : 0, cancellationToken))
                {
                    await TransitionAsync(taskId, DownloadTaskState.InsufficientStorage, DownloadFailureCode.InsufficientStorage, "可用磁盘空间不足，下载未开始", CancellationToken.None);
                    return;
                }

                task = await UpdateProgressAsync(taskId, offset, expectedTotal, DownloadTaskState.Downloading, "正在下载", cancellationToken);
                if (task is null || task.TemporaryFilePath is null) return;
                var mode = offset > 0 ? FileMode.Append : FileMode.Create;
                await using var destination = new FileStream(task.TemporaryFilePath, mode, FileAccess.Write, FileShare.Read, _options.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[Math.Clamp(_options.BufferSize, 8192, 1024 * 1024)];
                var received = offset;
                var lastPersisted = DateTimeOffset.UtcNow;
                while (true)
                {
                    var count = await response.Content.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (DateTimeOffset.UtcNow - lastPersisted >= _options.EffectiveProgressPersistenceInterval)
                    {
                        await UpdateProgressAsync(taskId, received, expectedTotal, DownloadTaskState.Downloading, "正在下载", cancellationToken);
                        lastPersisted = DateTimeOffset.UtcNow;
                    }
                }
                await destination.FlushAsync(cancellationToken);
                if (received <= 0 || expectedTotal is { } expected && received != expected)
                    throw new InvalidDataException("下载内容不完整");
                task = await UpdateProgressAsync(taskId, received, expectedTotal ?? received, DownloadTaskState.Downloading, "正在完成下载", cancellationToken);
            }

            if (task?.TemporaryFilePath is null || task.FilePath is null) throw new InvalidDataException("下载目标无效");
            ValidateTemporaryPath(task, task.TemporaryFilePath);
            ValidateFinalPath(task, task.FilePath);
            File.Move(task.TemporaryFilePath, task.FilePath, false);
            await TransitionAsync(taskId, DownloadTaskState.Completed, DownloadFailureCode.None, "下载完成", CancellationToken.None, clearTemporaryPath: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (UnauthorizedAccessException)
        {
            await TransitionIfActiveAsync(taskId, DownloadTaskState.Failed, DownloadFailureCode.FileAccessDenied, "无法写入下载目录，请重新选择", CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            await TransitionIfActiveAsync(taskId, DownloadTaskState.Failed, DownloadFailureCode.InvalidResponse, "下载服务返回了无效内容", CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            await TransitionIfActiveAsync(taskId, DownloadTaskState.Failed, DownloadFailureCode.NetworkUnavailable, "网络不可用或下载服务暂时无法访问", CancellationToken.None);
        }
        catch (IOException)
        {
            var current = await GetTaskAsync(taskId, CancellationToken.None);
            var hasSpace = current is not null && await HasStorageAsync(current.DestinationDirectory, 0, CancellationToken.None);
            await TransitionIfActiveAsync(taskId,
                hasSpace ? DownloadTaskState.Failed : DownloadTaskState.InsufficientStorage,
                hasSpace ? DownloadFailureCode.SourceUnavailable : DownloadFailureCode.InsufficientStorage,
                hasSpace ? "下载文件暂时无法写入" : "可用磁盘空间不足，下载已停止",
                CancellationToken.None);
        }
        catch (Exception)
        {
            await TransitionIfActiveAsync(taskId, DownloadTaskState.Failed, DownloadFailureCode.Unknown, "下载任务未能完成，请稍后重试", CancellationToken.None);
        }
        finally
        {
            if (acquiredWorker) _workerGate.Release();
            if (_runtimes.TryRemove(taskId, out var runtime)) runtime.Cancellation.Dispose();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;
            var state = await _persistence.LoadAsync(cancellationToken);
            _destinationDirectory = NormalizeDirectory(state.DestinationDirectory) ?? _options.EffectiveDefaultDestinationDirectory;
            var changed = false;
            foreach (var stored in state.Tasks)
            {
                if (string.IsNullOrWhiteSpace(stored.Id) || string.IsNullOrWhiteSpace(stored.Identity.NativeId)) { changed = true; continue; }
                var task = stored;
                if (task.State is DownloadTaskState.Queued or DownloadTaskState.Resolving or DownloadTaskState.Downloading)
                {
                    task = task with { State = DownloadTaskState.Paused, SafeMessage = "应用重启后任务已暂停，可继续下载", UpdatedAt = DateTimeOffset.UtcNow };
                    changed = true;
                }
                if (!StoredPathsAreSafe(task))
                {
                    task = task with { FilePath = null, TemporaryFilePath = null, State = DownloadTaskState.Failed, FailureCode = DownloadFailureCode.InvalidDestination, SafeMessage = "下载记录中的文件路径无效", UpdatedAt = DateTimeOffset.UtcNow };
                    changed = true;
                }
                _tasks[task.Id] = task;
            }
            _loaded = true;
            if (changed) await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }
    }

    private async Task<DownloadTaskSnapshot?> PreparePathsAsync(string taskId, DownloadSource source, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_tasks.TryGetValue(taskId, out var task)) return null;
            if (task.State is DownloadTaskState.Paused or DownloadTaskState.Cancelled) return null;
            if (task.FilePath is not null && task.TemporaryFilePath is not null)
            {
                ValidateFinalPath(task, task.FilePath);
                ValidateTemporaryPath(task, task.TemporaryFilePath);
                return task;
            }
            var extension = NormalizeExtension(source.FileExtension);
            var name = SanitizeFileName($"{task.Artist} - {task.Title}");
            var suffix = task.Id[..Math.Min(8, task.Id.Length)];
            var finalPath = SafeCombine(task.DestinationDirectory, $"{name} - {suffix}{extension}");
            var temporaryPath = finalPath + ".beans-part";
            if (File.Exists(finalPath)) throw new IOException("目标文件已经存在");
            task = task with { FilePath = finalPath, TemporaryFilePath = temporaryPath, TotalBytes = source.TotalBytes, UpdatedAt = DateTimeOffset.UtcNow };
            _tasks[taskId] = task;
            await SaveLockedAsync(cancellationToken);
            return task;
        }
        finally { _stateGate.Release(); }
    }

    private async Task<DownloadTaskSnapshot?> TransitionAsync(
        string taskId,
        DownloadTaskState state,
        DownloadFailureCode failureCode,
        string message,
        CancellationToken cancellationToken,
        bool clearTemporaryPath = false)
    {
        await _stateGate.WaitAsync(cancellationToken);
        DownloadTaskSnapshot? updated = null;
        try
        {
            if (!_tasks.TryGetValue(taskId, out var current) || current.State is DownloadTaskState.Paused or DownloadTaskState.Cancelled) return null;
            updated = current with
            {
                State = state,
                FailureCode = failureCode,
                SafeMessage = message,
                TemporaryFilePath = clearTemporaryPath ? null : current.TemporaryFilePath,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _tasks[taskId] = updated;
            await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }
        RaiseTasksChanged();
        return updated;
    }

    private async Task TransitionIfActiveAsync(string taskId, DownloadTaskState state, DownloadFailureCode code, string message, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        var changed = false;
        try
        {
            if (_tasks.TryGetValue(taskId, out var current) && current.State is not (DownloadTaskState.Paused or DownloadTaskState.Cancelled or DownloadTaskState.Completed))
            {
                _tasks[taskId] = current with { State = state, FailureCode = code, SafeMessage = message, UpdatedAt = DateTimeOffset.UtcNow };
                await SaveLockedAsync(cancellationToken);
                changed = true;
            }
        }
        finally { _stateGate.Release(); }
        if (changed) RaiseTasksChanged();
    }

    private async Task<DownloadTaskSnapshot?> UpdateProgressAsync(string taskId, long bytes, long? total, DownloadTaskState state, string message, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        DownloadTaskSnapshot? updated = null;
        try
        {
            if (!_tasks.TryGetValue(taskId, out var current) || current.State is DownloadTaskState.Paused or DownloadTaskState.Cancelled) return null;
            updated = current with { BytesReceived = Math.Max(0, bytes), TotalBytes = total, State = state, FailureCode = DownloadFailureCode.None, SafeMessage = message, UpdatedAt = DateTimeOffset.UtcNow };
            _tasks[taskId] = updated;
            await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }
        RaiseTasksChanged();
        return updated;
    }

    private async Task<DownloadOperationResult> SetControlStateAsync(
        string taskId,
        Func<DownloadTaskSnapshot, bool> allowed,
        DownloadTaskState state,
        DownloadFailureCode failureCode,
        string message,
        CancellationToken cancellationToken)
    {
        DownloadTaskSnapshot? updated;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_tasks.TryGetValue(taskId, out var current)) return DownloadOperationResult.Failure("下载任务不存在");
            if (!allowed(current)) return DownloadOperationResult.Failure("当前任务状态不支持此操作");
            updated = current with { State = state, FailureCode = failureCode, SafeMessage = message, UpdatedAt = DateTimeOffset.UtcNow };
            _tasks[taskId] = updated;
            await SaveLockedAsync(cancellationToken);
        }
        finally { _stateGate.Release(); }
        RaiseTasksChanged();
        return DownloadOperationResult.Success(updated, message);
    }

    private async Task<DownloadTaskSnapshot?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try { return _tasks.GetValueOrDefault(taskId); }
        finally { _stateGate.Release(); }
    }

    private async Task StopRuntimeAsync(string taskId)
    {
        if (!_runtimes.TryGetValue(taskId, out var runtime)) return;
        runtime.Cancellation.Cancel();
        try { await runtime.Task; }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> HasStorageAsync(string directory, long requiredBytes, CancellationToken cancellationToken)
    {
        var available = await _storageProbe.GetAvailableBytesAsync(directory, cancellationToken);
        if (available is null) return true;
        var required = Math.Max(0, requiredBytes);
        return available.Value >= required + Math.Max(0, _options.ReservedFreeSpaceBytes);
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken) =>
        await _persistence.SaveAsync(new DownloadStoreState { DestinationDirectory = _destinationDirectory, Tasks = _tasks.Values.ToList() }, cancellationToken);

    private static void ValidateSource(DownloadSource source)
    {
        if (!source.Uri.IsAbsoluteUri || !string.Equals(source.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载源无效");
        _ = NormalizeExtension(source.FileExtension);
        if (source.TotalBytes is <= 0) throw new InvalidDataException("下载大小无效");
        if (!IsAllowedMediaType(source.MediaType)) throw new InvalidDataException("下载类型无效");
    }

    private static void ValidateResponse(DownloadTransportResponse response, DownloadSource source)
    {
        if (!response.Content.CanRead) throw new InvalidDataException("下载响应不可读");
        if (!IsAllowedMediaType(response.MediaType)) throw new InvalidDataException("下载响应类型无效");
        if (response.TotalBytes is <= 0 || source.TotalBytes is { } expected && response.TotalBytes is { } actual && expected != actual)
            throw new InvalidDataException("下载响应大小无效");
    }

    private static bool IsAllowedMediaType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (!extension.StartsWith('.')) extension = "." + extension;
        if (!AllowedExtensions.Contains(extension)) throw new InvalidDataException("下载文件类型不受支持");
        return extension.ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        while (cleaned.Contains("  ", StringComparison.Ordinal)) cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        while (cleaned.Contains("..", StringComparison.Ordinal)) cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "未命名曲目";
        if (ReservedFileNames.Contains(cleaned)) cleaned = "_" + cleaned;
        if (cleaned.Length > 120) cleaned = cleaned[..120].TrimEnd();
        return cleaned;
    }

    private static string SafeCombine(string directory, string fileName)
    {
        var normalized = NormalizeDirectory(directory) ?? throw new InvalidDataException("下载目录无效");
        var path = Path.GetFullPath(Path.Combine(normalized, fileName));
        if (!IsPathInside(normalized, path)) throw new InvalidDataException("下载路径越界");
        return path;
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(directory)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IsPathInside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StoredPathsAreSafe(DownloadTaskSnapshot task)
    {
        try
        {
            var directory = NormalizeDirectory(task.DestinationDirectory);
            if (directory is null) return false;
            if (task.FilePath is null) return task.TemporaryFilePath is null;
            if (!IsManagedFinalPath(task, task.FilePath)) return false;
            return task.TemporaryFilePath is null || IsManagedTemporaryPath(task, task.TemporaryFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsManagedFinalPath(DownloadTaskSnapshot task, string path)
    {
        if (!IsPathInside(task.DestinationDirectory, path) || !AllowedExtensions.Contains(Path.GetExtension(path))) return false;
        var suffix = " - " + task.Id[..Math.Min(8, task.Id.Length)];
        return Path.GetFileNameWithoutExtension(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedTemporaryPath(DownloadTaskSnapshot task, string path) =>
        task.FilePath is not null &&
        IsPathInside(task.DestinationDirectory, path) &&
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(task.FilePath) + ".beans-part", StringComparison.OrdinalIgnoreCase);

    private static void ValidateFinalPath(DownloadTaskSnapshot task, string path)
    {
        if (!IsManagedFinalPath(task, path)) throw new InvalidDataException("下载路径越界");
    }

    private static void ValidateTemporaryPath(DownloadTaskSnapshot task, string path)
    {
        if (!IsManagedTemporaryPath(task, path)) throw new InvalidDataException("下载临时路径越界");
    }

    private static long GetPartialLength(DownloadTaskSnapshot task)
    {
        if (task.TemporaryFilePath is null) return 0;
        ValidateTemporaryPath(task, task.TemporaryFilePath);
        return File.Exists(task.TemporaryFilePath) ? new FileInfo(task.TemporaryFilePath).Length : 0;
    }

    private static void TruncatePartial(DownloadTaskSnapshot task)
    {
        if (task.TemporaryFilePath is null) return;
        ValidateTemporaryPath(task, task.TemporaryFilePath);
        using var stream = new FileStream(task.TemporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    private static void TryDeletePartial(DownloadTaskSnapshot task)
    {
        try
        {
            if (task.TemporaryFilePath is null) return;
            ValidateTemporaryPath(task, task.TemporaryFilePath);
            if (File.Exists(task.TemporaryFilePath)) File.Delete(task.TemporaryFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private void RaiseTasksChanged() => TasksChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var runtimes = _runtimes.Values.ToArray();
        foreach (var runtime in runtimes) runtime.Cancellation.Cancel();
        try { Task.WhenAll(runtimes.Select(runtime => runtime.Task)).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        if (runtimes.All(runtime => runtime.Task.IsCompleted))
        {
            _workerGate.Dispose();
            _stateGate.Dispose();
        }
    }

    private sealed class TaskRuntime(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
