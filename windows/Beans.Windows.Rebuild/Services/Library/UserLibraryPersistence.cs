using System.Text.Json;

namespace Beans.Windows.Rebuild.Services.Library;

internal sealed class UserLibraryState
{
    public int SchemaVersion { get; set; } = 1;
    public List<FavoriteEntry> Favorites { get; set; } = [];
    public List<PlaybackHistoryEntry> History { get; set; } = [];
}

internal sealed record UserLibraryLoadResult(UserLibraryState State, LibraryRecoveryStatus RecoveryStatus);

internal sealed class UserLibraryPersistence
{
    private readonly string _path;
    private readonly string _backupPath;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public UserLibraryPersistence(string path)
    {
        _path = path;
        _backupPath = path + ".bak";
    }

    public async Task<UserLibraryLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new(new UserLibraryState(), LibraryRecoveryStatus.Normal);

        var primary = await TryLoadAsync(_path, cancellationToken);
        if (primary is not null)
            return new(primary, LibraryRecoveryStatus.Normal);

        var backup = await TryLoadAsync(_backupPath, cancellationToken);
        return backup is not null
            ? new(backup, LibraryRecoveryStatus.RestoredFromBackup)
            : new(new UserLibraryState(), LibraryRecoveryStatus.ResetAfterCorruption);
    }

    public async Task SaveAsync(UserLibraryState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_path) && await TryLoadAsync(_path, cancellationToken) is not null)
                File.Copy(_path, _backupPath, true);
            File.Move(temporary, _path, true);
            if (!File.Exists(_backupPath)) File.Copy(_path, _backupPath, false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private async Task<UserLibraryState?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var state = await JsonSerializer.DeserializeAsync<UserLibraryState>(stream, _options, cancellationToken);
            return state is { SchemaVersion: 1 } ? state : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
