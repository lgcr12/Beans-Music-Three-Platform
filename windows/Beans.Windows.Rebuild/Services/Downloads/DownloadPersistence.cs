using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.Downloads;

internal sealed class DownloadStoreState
{
    public string? DestinationDirectory { get; set; }
    public List<DownloadTaskSnapshot> Tasks { get; set; } = [];
}

internal sealed class DownloadPersistence
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DownloadPersistence(string path) => _path = path;

    public async Task<DownloadStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return new();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<DownloadStoreState>(stream, _options, cancellationToken) ?? new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(DownloadStoreState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, state, _options, cancellationToken);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }
}
