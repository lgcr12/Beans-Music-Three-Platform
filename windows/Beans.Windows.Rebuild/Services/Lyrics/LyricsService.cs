using System.Collections.Concurrent;
using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Lyrics;

public sealed class LyricsService : ILyricsService
{
    private static readonly TimeSpan OnlineLyricsLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan OnlineNegativeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnsupportedLifetime = TimeSpan.FromMinutes(5);
    private readonly IReadOnlyDictionary<PlatformId, ILyricsSourceAdapter> _adapters;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public LyricsService(IEnumerable<ILyricsSourceAdapter> adapters)
        : this(adapters, TimeProvider.System)
    {
    }

    public LyricsService(IEnumerable<ILyricsSourceAdapter> adapters, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _adapters = adapters
            .GroupBy(adapter => adapter.Platform)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public async Task<LyricsResult> GetLyricsAsync(LyricsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(request.CacheKey, out var cached))
        {
            if (cached.ExpiresAt > _timeProvider.GetUtcNow()) return cached.Result.AsCached();
            _cache.TryRemove(request.CacheKey, out _);
        }
        if (!_adapters.TryGetValue(request.Platform, out var adapter))
            return LyricsResult.Unsupported($"当前版本暂不支持{request.Platform.ToDisplayName()}歌词");

        try
        {
            var result = await adapter.GetLyricsAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.State is LyricsLoadState.Loaded or LyricsLoadState.NotFound or LyricsLoadState.Empty or LyricsLoadState.Unsupported)
                _cache[request.CacheKey] = new CacheEntry(
                    result with { IsFromCache = false }, GetExpiration(request, result));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return LyricsResult.Error();
        }
    }

    public void Invalidate(LyricsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _cache.TryRemove(request.CacheKey, out _);
    }

    private DateTimeOffset GetExpiration(LyricsRequest request, LyricsResult result)
    {
        if (request.Platform == PlatformId.Local) return DateTimeOffset.MaxValue;
        var lifetime = result.State switch
        {
            LyricsLoadState.Loaded => OnlineLyricsLifetime,
            LyricsLoadState.Unsupported => UnsupportedLifetime,
            _ => OnlineNegativeLifetime
        };
        return _timeProvider.GetUtcNow().Add(lifetime);
    }

    private sealed record CacheEntry(LyricsResult Result, DateTimeOffset ExpiresAt);
}
