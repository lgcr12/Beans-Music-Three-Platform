using Beans.Windows.Rebuild.Models;
using System.Text.Json.Serialization;

namespace Beans.Windows.Rebuild.Services.Downloads;

public enum DownloadTaskState
{
    Queued,
    Resolving,
    Downloading,
    Paused,
    Completed,
    Cancelled,
    Failed,
    AuthorizationRequired,
    Unsupported,
    InsufficientStorage
}

public enum DownloadFailureCode
{
    None,
    AuthorizationRequired,
    OfflineDownloadNotAllowed,
    SourceUnavailable,
    InvalidDestination,
    InvalidResponse,
    InsufficientStorage,
    FileAccessDenied,
    NetworkUnavailable,
    Cancelled,
    Unknown
}

public sealed record DownloadTaskSnapshot(
    string Id,
    MusicIdentity Identity,
    string Title,
    string Artist,
    string ArtworkUri,
    AudioQuality RequestedQuality,
    string DestinationDirectory,
    string? FilePath,
    string? TemporaryFilePath,
    long BytesReceived,
    long? TotalBytes,
    DownloadTaskState State,
    DownloadFailureCode FailureCode,
    string SafeMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    [JsonIgnore]
    public double ProgressPercent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
        : 0;

    [JsonIgnore]
    public string SourceText => Identity.Platform.ToDisplayName();
    [JsonIgnore]
    public string QualityText => RequestedQuality switch
    {
        AudioQuality.Standard => "标准",
        AudioQuality.High => "高品质",
        AudioQuality.VeryHigh => "超高品质",
        AudioQuality.Lossless => "无损",
        AudioQuality.HiRes => "Hi-Res",
        _ => "标准"
    };

    [JsonIgnore]
    public string StateText => State switch
    {
        DownloadTaskState.Queued => "等待中",
        DownloadTaskState.Resolving => "正在检查授权",
        DownloadTaskState.Downloading => "下载中",
        DownloadTaskState.Paused => "已暂停",
        DownloadTaskState.Completed => "已完成",
        DownloadTaskState.Cancelled => "已取消",
        DownloadTaskState.Failed => "失败",
        DownloadTaskState.AuthorizationRequired => "需要登录",
        DownloadTaskState.Unsupported => "暂不支持",
        DownloadTaskState.InsufficientStorage => "空间不足",
        _ => "未知"
    };

    [JsonIgnore]
    public string ProgressText => State is DownloadTaskState.Failed or DownloadTaskState.AuthorizationRequired or DownloadTaskState.Unsupported or DownloadTaskState.InsufficientStorage or DownloadTaskState.Cancelled
        ? SafeMessage
        : TotalBytes is > 0
            ? $"{FormatBytes(BytesReceived)} / {FormatBytes(TotalBytes.Value)}"
            : BytesReceived > 0 ? $"已下载 {FormatBytes(BytesReceived)}" : SafeMessage;

    [JsonIgnore]
    public bool CanPause => State is DownloadTaskState.Queued or DownloadTaskState.Resolving or DownloadTaskState.Downloading;
    [JsonIgnore]
    public bool CanResume => State == DownloadTaskState.Paused;
    [JsonIgnore]
    public bool CanCancel => State is DownloadTaskState.Queued or DownloadTaskState.Resolving or DownloadTaskState.Downloading or DownloadTaskState.Paused;
    [JsonIgnore]
    public bool CanRemove => State is DownloadTaskState.Completed or DownloadTaskState.Cancelled or DownloadTaskState.Failed or DownloadTaskState.AuthorizationRequired or DownloadTaskState.Unsupported or DownloadTaskState.InsufficientStorage;

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }
}

public sealed record DownloadEnqueueRequest(
    MusicTrack Track,
    string? DestinationDirectory = null,
    AudioQuality RequestedQuality = AudioQuality.Standard);

public sealed record DownloadOperationResult(bool IsSuccess, DownloadTaskSnapshot? Task, string SafeMessage)
{
    public static DownloadOperationResult Failure(string message) => new(false, null, message);
    public static DownloadOperationResult Success(DownloadTaskSnapshot task, string message) => new(true, task, message);
}

public sealed record DownloadManagerOptions(
    string? StoragePath = null,
    string? DefaultDestinationDirectory = null,
    int MaximumConcurrentDownloads = 2,
    long ReservedFreeSpaceBytes = 128L * 1024 * 1024,
    int BufferSize = 64 * 1024,
    TimeSpan? ProgressPersistenceInterval = null)
{
    public string EffectiveStoragePath => StoragePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeansMusic", "Rebuild", "downloads.json");

    public string EffectiveDefaultDestinationDirectory => DefaultDestinationDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Beans Music Downloads");

    public TimeSpan EffectiveProgressPersistenceInterval => ProgressPersistenceInterval ?? TimeSpan.FromMilliseconds(500);
}

public sealed record DownloadSourceRequest(MusicIdentity Identity, AudioQuality RequestedQuality);

public sealed record DownloadSource(
    Uri Uri,
    string FileExtension,
    string MediaType,
    long? TotalBytes,
    bool SupportsRangeRequests,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record DownloadSourceResult(
    bool IsSuccess,
    DownloadSource? Source,
    DownloadFailureCode FailureCode,
    string SafeMessage)
{
    public static DownloadSourceResult Success(DownloadSource source) =>
        new(true, source, DownloadFailureCode.None, "离线下载源已准备");

    public static DownloadSourceResult Failure(DownloadFailureCode code, string message) =>
        new(false, null, code, message);
}

public interface IDownloadSourceResolver
{
    Task<DownloadSourceResult> ResolveAsync(DownloadSourceRequest request, CancellationToken cancellationToken);
}

public interface IDownloadTransport
{
    Task<DownloadTransportResponse> OpenReadAsync(DownloadSource source, long offset, CancellationToken cancellationToken);
}

public interface IDownloadStorageProbe
{
    Task<long?> GetAvailableBytesAsync(string directory, CancellationToken cancellationToken);
}

public interface IDownloadManager : IDisposable
{
    event EventHandler? TasksChanged;
    string DestinationDirectory { get; }
    Task<IReadOnlyList<DownloadTaskSnapshot>> GetTasksAsync(CancellationToken cancellationToken = default);
    Task<DownloadOperationResult> ConfigureDestinationDirectoryAsync(string directory, CancellationToken cancellationToken = default);
    Task<DownloadOperationResult> EnqueueAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken = default);
    Task<DownloadOperationResult> PauseAsync(string taskId, CancellationToken cancellationToken = default);
    Task<DownloadOperationResult> ResumeAsync(string taskId, CancellationToken cancellationToken = default);
    Task<DownloadOperationResult> CancelAsync(string taskId, CancellationToken cancellationToken = default);
    Task<DownloadOperationResult> RemoveAsync(string taskId, CancellationToken cancellationToken = default);
}

public sealed class DownloadTransportResponse : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public DownloadTransportResponse(Stream content, long? totalBytes, bool isPartialContent, string? mediaType, IDisposable? owner = null)
    {
        Content = content;
        TotalBytes = totalBytes;
        IsPartialContent = isPartialContent;
        MediaType = mediaType;
        _owner = owner;
    }

    public Stream Content { get; }
    public long? TotalBytes { get; }
    public bool IsPartialContent { get; }
    public string? MediaType { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        _owner?.Dispose();
    }
}
