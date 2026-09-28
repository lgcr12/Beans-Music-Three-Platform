using System.Text.Json;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Playback;
namespace Beans.Windows.Rebuild.Services.Anime;

public sealed class AnimeUserState
{
    public HashSet<string> Subjects { get; set; } = [];
    public HashSet<string> Songs { get; set; } = [];
    public Dictionary<string, AnimeFavoriteSong> SongDetails { get; set; } = [];
    public bool ReduceMotion { get; set; }
    public bool Translation { get; set; } = true;
    public double FontSize { get; set; } = 28;
    public double LineSpacing { get; set; } = 20;
}
public sealed record AnimeFavoriteSong(string Id, string AnimeId, string Title, string Artist, AnimeSongType Type, int Season, string Version)
{
    public AnimeThemeSong ToSong() => new(Id, AnimeId, Title, Artist, Type, Season, [], Version);
}
public sealed class AnimeSession
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1,1);
    private readonly Dictionary<string,AnimeSubject> _playbackSubjects = [];
    public AnimeUserState State { get; private set; }
    public event EventHandler? Changed;
    public AnimeSession(string? path = null)
    {
        _path = path ?? @"D:\Apps\BeansMusic\Home\anime-user.json";
        try { State = JsonSerializer.Deserialize<AnimeUserState>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { State = new(); }
    }
    public void Register(AnimeSubject subject,IEnumerable<AnimeThemeSong> songs)
    {
        foreach(var item in songs.SelectMany(s => s.Matches)) _playbackSubjects[$"{item.Platform}:{item.NativeId}"] = subject;
    }
    public AnimeSubject? SubjectFor(PlaybackItem? item)
    {
        if (item is null) return null;
        var native = item.NativeId;
        var marker = item.Id.IndexOf(":track:",StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(native) && marker >= 0) native = item.Id[(marker+7)..];
        return _playbackSubjects.GetValueOrDefault($"{item.Platform}:{native}");
    }
    public async Task UpdateAsync(Action<AnimeUserState> update,CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = JsonSerializer.Deserialize<AnimeUserState>(JsonSerializer.Serialize(State))!;
            update(next);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path+".tmp",JsonSerializer.Serialize(next),ct);
            File.Move(_path+".tmp",_path,true);
            State = next;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public Task ToggleSubjectAsync(string id,CancellationToken ct = default) => UpdateAsync(s => {if(!s.Subjects.Add(id)) s.Subjects.Remove(id);},ct);
    public Task ToggleSongAsync(string id,CancellationToken ct = default) => UpdateAsync(s => {if(!s.Songs.Add(id)) s.Songs.Remove(id);},ct);
    public Task ToggleSongAsync(AnimeThemeSong song, CancellationToken ct = default) => UpdateAsync(s =>
    {
        if (s.Songs.Add(song.Id)) s.SongDetails[song.Id] = new(song.Id, song.AnimeId, song.Title, song.Artist, song.Type, song.Season, song.Version);
        else { s.Songs.Remove(song.Id); s.SongDetails.Remove(song.Id); }
    }, ct);
}
