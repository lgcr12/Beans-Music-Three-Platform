using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.Anime;

public sealed record AnimeCacheEntry<T>(DateTimeOffset SavedAt, T Value);

// Catalog metadata and stable identities only; no temporary playback addresses.
public sealed class AnimeDiskCache(string? root = null, TimeProvider? clock = null)
{
    private readonly string _root = root ?? @"D:\Apps\BeansMusic\Cache\Anime";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public bool IsFresh<T>(AnimeCacheEntry<T>? entry, TimeSpan ttl) => entry is not null && _clock.GetUtcNow() - entry.SavedAt < ttl;
    public async Task<AnimeCacheEntry<T>?> ReadAsync<T>(string key, CancellationToken ct)
    {
        try { return JsonSerializer.Deserialize<AnimeCacheEntry<T>>(await File.ReadAllTextAsync(FilePath(key), ct), _json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task WriteAsync<T>(string key, T value, CancellationToken ct)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_root);
            temp = FilePath(key) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new AnimeCacheEntry<T>(_clock.GetUtcNow(), value), _json), ct);
            File.Move(temp, FilePath(key), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { if (temp is not null && File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } } }
    }
    private string FilePath(string key) => Path.Combine(_root, string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_')) + ".json");
}
