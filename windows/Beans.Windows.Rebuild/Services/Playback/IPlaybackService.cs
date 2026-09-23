using System.Collections.ObjectModel;
using System.ComponentModel;
using Beans.Windows.Rebuild.Models;
using Microsoft.UI.Dispatching;

namespace Beans.Windows.Rebuild.Services.Playback;

public enum PlaybackRepeatMode { Off, All, One }

public sealed record PlaybackItem(
    string Id,
    string Title,
    string Artist,
    string Album,
    string ArtworkUri,
    string SourceUri,
    TimeSpan Duration,
    string SourceLabel = "本地音乐",
    string QualityLabel = "SQ",
    PlatformId Platform = PlatformId.Local,
    string? NativeId = null,
    SearchDataOrigin DataOrigin = SearchDataOrigin.Live,
    string? ProviderMediaId = null);

public interface IPlaybackService : INotifyPropertyChanged, IDisposable
{
    ObservableCollection<PlaybackItem> Queue { get; }
    PlaybackItem? Current { get; }
    string Title { get; }
    string Artist { get; }
    string Album { get; }
    string ArtworkUri { get; }
    string SourceLabel { get; }
    string QualityLabel { get; }
    string StatusText { get; }
    string PlayPauseGlyph { get; }
    string PositionText { get; }
    string DurationText { get; }
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    double VolumePercent { get; }
    bool IsPlaying { get; }
    bool IsShuffleEnabled { get; }
    bool IsFavorite { get; }
    PlaybackRepeatMode RepeatMode { get; }

    void AttachDispatcherQueue(DispatcherQueue dispatcherQueue);
    void Prepare(PlaybackItem item, bool replaceQueue = true);
    Task PlayAsync(PlaybackItem item, bool replaceQueue = false);
    Task<PlaybackOperationResult> PlaySearchResultAsync(SearchResultItem item, bool replaceQueue = true, CancellationToken cancellationToken = default);
    Task<PlaybackOperationResult> QueueSearchResultAsync(SearchResultItem item, bool playNext = false, CancellationToken cancellationToken = default);
    void TogglePlayPause();
    void Previous();
    void Next();
    void Seek(TimeSpan position);
    void BeginSeek();
    void EndSeek(TimeSpan position);
    void SetVolume(double percent);
    Task CycleQualityAsync(CancellationToken cancellationToken = default);
    Task SetQualityAsync(AudioQuality quality, CancellationToken cancellationToken = default);
    void ToggleShuffle();
    void CycleRepeatMode();
    void ToggleFavorite();
    void AddToQueue(PlaybackItem item);
    void AddNextToQueue(PlaybackItem item);
    void ClearQueue();
}
