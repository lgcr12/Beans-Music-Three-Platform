using System.Text.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;

namespace Beans.Windows.Rebuild.Services.BeansPlaylists;

public sealed record BeansPlaylist
{
    public BeansPlaylist() { }
    public BeansPlaylist(string id, string title, IReadOnlyList<LibraryMediaSnapshot> tracks, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        (Id, Title, Tracks, CreatedAt, UpdatedAt) = (id, title, tracks, createdAt, updatedAt);

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public IReadOnlyList<LibraryMediaSnapshot> Tracks { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int TrackCount => Tracks.Count;
    public string TrackCountText => $"{TrackCount} 首";
}

public interface IBeansPlaylistService
{
    Task<IReadOnlyList<BeansPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<BeansPlaylist> CreateAsync(string title, CancellationToken cancellationToken = default);
    Task<bool> AddTrackAsync(string playlistId, LibraryMediaSnapshot track, CancellationToken cancellationToken = default);
    Task<int> AddTracksAsync(string playlistId, IReadOnlyCollection<LibraryMediaSnapshot> tracks, CancellationToken cancellationToken = default);
}

public sealed class JsonBeansPlaylistService : IBeansPlaylistService
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonBeansPlaylistService(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic", "Rebuild", "beans-playlists.json"));

    public async Task<IReadOnlyList<BeansPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        // Reading an already committed snapshot must not require write access to the
        // sidecar lock file. Some restricted launch contexts can read LocalAppData but
        // cannot create/open the lock for writing; treating that as an empty library
        // made valid playlists appear to disappear. Mutations still take the lock.
        try { return await LoadLockedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<BeansPlaylist> CreateAsync(string title, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTitle(title);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireFileLockAsync(cancellationToken);
            var values = (await LoadLockedAsync(cancellationToken)).ToList();
            var now = DateTimeOffset.UtcNow;
            var playlist = new BeansPlaylist(Guid.NewGuid().ToString("N"), normalized, [], now, now);
            values.Insert(0, playlist);
            await SaveLockedAsync(values, cancellationToken);
            return playlist;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddTrackAsync(string playlistId, LibraryMediaSnapshot track, CancellationToken cancellationToken = default)
    {
        if (await AddTracksAsync(playlistId, [track], cancellationToken) > 0) return true;
        return (await GetPlaylistsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == playlistId)?.Tracks
            .Any(item => item.StableKey.Equals(track.StableKey, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public async Task<int> AddTracksAsync(string playlistId, IReadOnlyCollection<LibraryMediaSnapshot> tracks, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || tracks is null || tracks.Count == 0) return 0;
        var validTracks = tracks
            .Where(track => track.Kind == LibraryItemKind.Track && track.Platform is not (PlatformId.Beans or PlatformId.Local))
            .GroupBy(track => track.StableKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (validTracks.Length == 0) return 0;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireFileLockAsync(cancellationToken);
            var values = (await LoadLockedAsync(cancellationToken)).ToList();
            var index = values.FindIndex(item => item.Id == playlistId);
            if (index < 0) return 0;
            var playlist = values[index];
            var existing = playlist.Tracks.Select(item => item.StableKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var additions = validTracks.Where(track => existing.Add(track.StableKey)).ToArray();
            if (additions.Length == 0) return 0;
            values[index] = playlist with { Tracks = playlist.Tracks.Concat(additions).ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
            await SaveLockedAsync(values, cancellationToken);
            return additions.Length;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<BeansPlaylist>> LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return File.Exists(_path + ".bak") ? await ReadFileAsync(_path + ".bak", cancellationToken) : [];
        try
        {
            return await ReadFileAsync(_path, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // A bad file is not an empty library. Refuse mutations unless a valid backup exists.
            if (File.Exists(_path + ".bak")) return await ReadFileAsync(_path + ".bak", cancellationToken);
            throw new InvalidDataException("Beans 歌单文件无法读取，已保留原文件。", ex);
        }
    }

    private async Task<IReadOnlyList<BeansPlaylist>> ReadFileAsync(string path, CancellationToken ct)
    {
        // File.Replace publishes a complete new snapshot atomically. Allow a writer to
        // replace the file while an existing reader finishes consuming the old handle.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var values = await JsonSerializer.DeserializeAsync<List<BeansPlaylist>>(stream, _options, ct);
        if (values is null || values.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Title) || p.Tracks is null || p.Tracks.Any(t => t is null)) ||
            values.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidDataException("Beans 歌单文件内容不完整。");
        return values;
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50, ct); }
        }
    }

    private async Task SaveLockedAsync(IReadOnlyList<BeansPlaylist> values, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, values, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
            {
                var validPrimary = true;
                try { await ReadFileAsync(_path, cancellationToken); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { validPrimary = false; }
                // Never replace a good backup with a corrupt primary during recovery.
                var backup = validPrimary ? _path + ".bak" : _path + ".corrupt-" + Guid.NewGuid().ToString("N");
                File.Replace(temporary, _path, backup);
            }
            else
            {
                File.Move(temporary, _path);
                File.Copy(_path, _path + ".bak", true);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string NormalizeTitle(string title)
    {
        var value = new string((title ?? string.Empty).Trim().Where(character => !char.IsControl(character)).ToArray());
        if (value.Length is 0 or > 80) throw new ArgumentException("歌单名称不能为空且不能超过 80 个字符", nameof(title));
        return value;
    }
}
