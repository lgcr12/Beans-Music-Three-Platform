using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Playback;

public enum PlaybackRestriction
{
    None,
    RequiresAuthorization,
    SubscriptionRequired,
    NotImplemented,
    RegionRestricted,
    FileUnavailable,
    ProviderUnavailable
}

public sealed record PlaybackSourceRequest(
    MusicIdentity Identity,
    AudioQuality RequestedQuality = AudioQuality.Standard,
    bool AllowCachedSource = true,
    string? ProviderMediaId = null);

public sealed record PlaybackSourceResult(
    bool IsSuccess,
    PlaybackSource? Source,
    PlaybackRestriction Restriction,
    string SafeMessage)
{
    public static PlaybackSourceResult Unavailable(PlaybackRestriction restriction, string message) =>
        new(false, null, restriction, message);
}

public interface IPlaybackSourceResolver
{
    PlatformId Platform { get; }
    Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken);
}

public sealed class OnlinePlaybackBoundaryResolver(PlatformId platform) : IPlaybackSourceResolver
{
    public PlatformId Platform { get; } = platform;

    public Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = Platform is PlatformId.QqMusic or PlatformId.NetEaseMusic
            ? "请先登录并获取该平台的官方播放源"
            : "当前来源暂不支持播放";
        return Task.FromResult(PlaybackSourceResult.Unavailable(PlaybackRestriction.RequiresAuthorization, message));
    }
}
