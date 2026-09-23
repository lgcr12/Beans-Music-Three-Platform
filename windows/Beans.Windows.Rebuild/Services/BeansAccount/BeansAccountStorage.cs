using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.BeansAccount;

public sealed record BeansAccountMetadata(
    Uri? Server = null,
    Guid DeviceId = default,
    string DeviceName = "",
    Guid? PendingRegistrationId = null,
    DateTimeOffset? PendingRegistrationExpiresAt = null,
    Guid? AccountId = null,
    string DisplayName = "",
    string Email = "",
    DateTimeOffset? LastSyncedAt = null,
    long SyncCursor = 0);

public interface IBeansAccountMetadataStore
{
    Task<BeansAccountMetadata> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(BeansAccountMetadata metadata, CancellationToken cancellationToken = default);
}

public sealed class JsonBeansAccountMetadataStore : IBeansAccountMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public JsonBeansAccountMetadataStore(string? serviceDirectory = null)
    {
        var directory = serviceDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeansMusic",
            "Rebuild",
            "BeansAccount");
        _path = Path.Combine(directory, "account-metadata.json");
    }

    public async Task<BeansAccountMetadata> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new BeansAccountMetadata(DeviceId: Guid.NewGuid());
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<BeansAccountMetadata>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new BeansAccountMetadata(DeviceId: Guid.NewGuid());
        }
        catch (IOException)
        {
            return new BeansAccountMetadata(DeviceId: Guid.NewGuid());
        }
        catch (JsonException)
        {
            return new BeansAccountMetadata(DeviceId: Guid.NewGuid());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(BeansAccountMetadata metadata, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record BeansLocalSyncState(
    long Cursor,
    IReadOnlyList<BeansSyncEnvelope> Pending,
    IReadOnlyList<BeansSyncEnvelope> Mirror);

public interface IBeansEncryptedSyncStore
{
    Task<BeansLocalSyncState> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(BeansLocalSyncState state, CancellationToken cancellationToken = default);
}

public sealed class JsonBeansEncryptedSyncStore : IBeansEncryptedSyncStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public JsonBeansEncryptedSyncStore(string? serviceDirectory = null)
    {
        var directory = serviceDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeansMusic",
            "Rebuild",
            "BeansAccount");
        _path = Path.Combine(directory, "encrypted-sync.json");
    }

    public async Task<BeansLocalSyncState> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return Empty();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<BeansLocalSyncState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? Empty();
        }
        catch (IOException) { return Empty(); }
        catch (JsonException) { return Empty(); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(BeansLocalSyncState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, true);
        }
        finally { _gate.Release(); }
    }

    private static BeansLocalSyncState Empty() => new(0, [], []);
}
