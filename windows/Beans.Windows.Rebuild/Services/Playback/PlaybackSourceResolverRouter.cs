using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Playback;

/// <summary>
/// Routes an online playback request to exactly one platform resolver.
/// Keeping this boundary separate from the platform implementations prevents
/// the DI container from accidentally resolving an arbitrary last registration.
/// </summary>
public interface IPlaybackSourceResolverRouter
{
    Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken);
}

public sealed class PlaybackSourceResolverRouter : IPlaybackSourceResolverRouter
{
    private readonly IReadOnlyDictionary<PlatformId, IPlaybackSourceResolver> _resolvers;

    public PlaybackSourceResolverRouter(IEnumerable<IPlaybackSourceResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        var entries = resolvers.ToArray();
        if (entries.Any(resolver => resolver is null)) throw new ArgumentException("播放解析器不能为空", nameof(resolvers));
        if (entries.Any(resolver => resolver.Platform == PlatformId.Local))
            throw new ArgumentException("本地播放不应注册在线解析器", nameof(resolvers));

        _resolvers = entries.ToDictionary(resolver => resolver.Platform);
    }

    public Task<PlaybackSourceResult> ResolveAsync(PlaybackSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_resolvers.TryGetValue(request.Identity.Platform, out var resolver))
        {
            return Task.FromResult(PlaybackSourceResult.Unavailable(
                PlaybackRestriction.NotImplemented,
                $"{request.Identity.Platform.ToDisplayName()}暂不支持在线播放"));
        }

        return resolver.ResolveAsync(request, cancellationToken);
    }
}
