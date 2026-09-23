using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;

namespace Beans.Windows.Rebuild.Services.Lyrics;

public enum LyricsLoadState
{
    Loaded,
    NotFound,
    Empty,
    Unsupported,
    Error
}

public sealed record LyricsRequest(
    PlatformId Platform,
    string NativeId,
    string StableId,
    string? AudioPath = null)
{
    public string CacheKey => $"{Platform.ToStableId()}:{NativeId}";

    public static LyricsRequest FromPlaybackItem(PlaybackItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var marker = item.Id.IndexOf(":track:", StringComparison.OrdinalIgnoreCase);
        if (marker > 0 && PlatformIdExtensions.TryParseStableId(item.Id[..marker], out var identifiedPlatform))
        {
            var nativeId = item.Id[(marker + ":track:".Length)..];
            return new LyricsRequest(identifiedPlatform, nativeId, item.Id, GetLocalPath(item.SourceUri));
        }

        var platform = item.SourceLabel.Contains("QQ", StringComparison.OrdinalIgnoreCase)
            ? PlatformId.QqMusic
            : item.SourceLabel.Contains("网易", StringComparison.OrdinalIgnoreCase)
                ? PlatformId.NetEaseMusic
                : PlatformId.Local;
        return new LyricsRequest(platform, item.Id, item.Id, GetLocalPath(item.SourceUri));
    }

    private static string? GetLocalPath(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) return uri.LocalPath;
        return Path.IsPathRooted(source) ? source : null;
    }
}

public sealed record LyricsResult(
    LyricsLoadState State,
    LyricDocument? Document,
    string SafeMessage,
    bool IsFromCache = false)
{
    public bool IsSuccess => State == LyricsLoadState.Loaded && Document is not null;

    public static LyricsResult Loaded(LyricDocument document) => new(
        LyricsLoadState.Loaded,
        document,
        document.IsInstrumental ? "当前歌曲为纯音乐" : "歌词已加载");

    public static LyricsResult NotFound(string message = "当前歌曲没有可显示的歌词内容") =>
        new(LyricsLoadState.NotFound, null, message);

    public static LyricsResult Empty(string message = "歌词文件没有可显示的时间轴内容") =>
        new(LyricsLoadState.Empty, null, message);

    public static LyricsResult Unsupported(string message) =>
        new(LyricsLoadState.Unsupported, null, message);

    public static LyricsResult Error(string message = "歌词暂时无法加载，请稍后重试") =>
        new(LyricsLoadState.Error, null, message);

    public LyricsResult AsCached() => this with { IsFromCache = true };
}

public interface ILyricsSourceAdapter
{
    PlatformId Platform { get; }
    Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken);
}

public interface ILyricsService
{
    Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken = default);
    void Invalidate(LyricsRequest request);
}
