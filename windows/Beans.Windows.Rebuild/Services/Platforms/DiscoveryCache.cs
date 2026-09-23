using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Platforms;

public sealed class DiscoveryCache : IDiscoveryCache
{
    private const int Capacity = 24;
    private readonly object _gate = new();
    private readonly Dictionary<DiscoveryCacheKey, CacheEntry> _entries = [];

    public bool TryGet(DiscoveryCacheKey key, out PlatformDiscoveryContent? content)
    {
        if (TryGetValue<PlatformDiscoveryContent>(key, false, out var cached) && cached is not null)
        {
            content = cached.Value.IsPreview
                ? cached.Value
                : cached.Value with
                {
                    DataOrigin = DiscoveryDataOrigin.CacheFresh,
                    IsStale = false,
                    SafeStatusText = "缓存内容 · 已是最新"
                };
            return true;
        }

        content = null;
        return false;
    }

    public void Set(DiscoveryCacheKey key, PlatformDiscoveryContent content, TimeSpan lifetime) =>
        SetValue(key, content, lifetime);

    public bool TryGetValue<T>(DiscoveryCacheKey key, bool includeExpired, out DiscoveryCacheValue<T>? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Value is not T typedValue)
            {
                value = null;
                return false;
            }

            var isStale = entry.ExpiresAt <= DateTimeOffset.UtcNow;
            if (isStale && !includeExpired)
            {
                value = null;
                return false;
            }

            value = new DiscoveryCacheValue<T>(typedValue, entry.StoredAt, entry.ExpiresAt, isStale);
            return true;
        }
    }

    public void SetValue<T>(DiscoveryCacheKey key, T value, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_gate)
        {
            if (_entries.Count >= Capacity)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.StoredAt).First().Key;
                _entries.Remove(oldest);
            }
            var now = DateTimeOffset.UtcNow;
            _entries[key] = new CacheEntry(value, now, now.Add(lifetime));
        }
    }

    public void RemovePlatform(string platformId)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(key => key.PlatformId == platformId).ToArray()) _entries.Remove(key);
        }
    }

    private sealed record CacheEntry(object Value, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt);
}
