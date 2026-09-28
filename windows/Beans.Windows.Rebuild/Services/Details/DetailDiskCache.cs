using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.Details;

// Only anonymous public metadata is persisted here, never credentials or playback URLs.
internal sealed class DetailDiskCache(string directory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private string PathFor(OnlineMusicDetailQuery query) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{query.Platform}:{query.Kind}:{query.NativeId}"))) + ".json");

    public async Task<OnlineMusicDetailContent?> ReadAsync(OnlineMusicDetailQuery query, CancellationToken token)
    {
        try
        {
            var path = PathFor(query);
            if (!File.Exists(path) || new FileInfo(path).Length > 12 * 1024 * 1024) return null;
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<OnlineMusicDetailContent>(stream, Options, token).ConfigureAwait(false);
            return value is not null && value.Platform == query.Platform && value.Kind == query.Kind &&
                value.NativeId == query.NativeId && value.Tracks is not null && value.RelatedCollections is not null &&
                value.LoadedAt > DateTimeOffset.UtcNow.AddDays(-7) ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task WriteAsync(OnlineMusicDetailQuery query, OnlineMusicDetailContent value, CancellationToken token)
    {
        var path = PathFor(query);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, Options, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public void Remove(OnlineMusicDetailQuery query)
    {
        try { File.Delete(PathFor(query)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
