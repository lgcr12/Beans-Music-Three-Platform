using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.LocalMusic;

internal sealed class LocalMusicState
{
    public List<LocalMusicFolder> Folders { get; set; } = [];
    public List<LocalTrack> Tracks { get; set; } = [];
    public List<LocalPlayHistoryEntry> History { get; set; } = [];
    public LocalMusicState() { }
    public LocalMusicState(List<LocalMusicFolder> folders, List<LocalTrack> tracks, List<LocalPlayHistoryEntry> history) => (Folders, Tracks, History) = (folders, tracks, history);
}

internal sealed class LocalMusicPersistence
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LocalMusicPersistence(LocalMusicCatalogOptions options) => _path = options.EffectiveStoragePath;

    public async Task<LocalMusicState> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return new();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<LocalMusicState>(stream, _options, cancellationToken) ?? new();
        }
        catch (JsonException) { return new(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LocalMusicState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, state, _options, cancellationToken);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }
}
