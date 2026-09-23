using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Beans.Windows.Rebuild.Models;
using Beans.Windows.Rebuild.Services.Library;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Beans.Windows.Rebuild.Services.Playback;

public sealed class PlaybackService : IPlaybackService
{
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly ILogger<PlaybackService> _logger;
    private readonly IPlaybackSourceResolverRouter? _sourceResolver;
    private readonly IUserLibraryService? _userLibrary;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _positionTimer;
    private PlaybackItem? _current;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volumePercent = 72;
    private bool _isPlaying;
    private bool _isShuffleEnabled;
    private bool _isFavorite;
    private bool _isSeeking;
    private string _statusText = "播放器就绪";
    private bool _hasRetriedCurrentSource;
    private int _sourceGeneration;
    private int _recordedHistoryGeneration = -1;
    private PlaybackRepeatMode _repeatMode = PlaybackRepeatMode.Off;

    public PlaybackService(
        ILogger<PlaybackService> logger,
        IPlaybackSourceResolverRouter? sourceResolver = null,
        IUserLibraryService? userLibrary = null)
    {
        _logger = logger;
        _sourceResolver = sourceResolver;
        _userLibrary = userLibrary;
        _mediaPlayer.CommandManager.IsEnabled = true;
        _mediaPlayer.Volume = _volumePercent / 100d;
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<PlaybackItem> Queue { get; } = [];
    public PlaybackItem? Current { get => _current; private set => Set(ref _current, value); }
    public string Title => Current?.Title ?? "选择一首歌曲";
    public string Artist => Current?.Artist ?? "Beans Music";
    public string Album => Current?.Album ?? "";
    public string ArtworkUri => Current?.ArtworkUri ?? "ms-appx:///Assets/Home/hero-mountain-lake.jpg";
    public string SourceLabel => Current?.SourceLabel ?? "本地音乐";
    public string QualityLabel => Current?.QualityLabel ?? "SQ";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";
    public string PositionText => FormatTime(PositionSeconds);
    public string DurationText => FormatTime(DurationSeconds);
    public double PositionSeconds { get => _positionSeconds; private set => Set(ref _positionSeconds, value); }
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, value); }
    public double VolumePercent { get => _volumePercent; private set => Set(ref _volumePercent, value); }
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }
    public bool IsShuffleEnabled { get => _isShuffleEnabled; private set => Set(ref _isShuffleEnabled, value); }
    public bool IsFavorite { get => _isFavorite; private set => Set(ref _isFavorite, value); }
    public PlaybackRepeatMode RepeatMode { get => _repeatMode; private set => Set(ref _repeatMode, value); }

    public void AttachDispatcherQueue(DispatcherQueue dispatcherQueue)
    {
        if (_positionTimer is not null) return;
        _dispatcher = dispatcherQueue;
        _positionTimer = dispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
        _positionTimer.Tick += (_, _) => RefreshTimeline();
        _positionTimer.Start();
    }

    public Task PlayAsync(PlaybackItem item, bool replaceQueue = false)
    {
        StatusText = "正在打开音频…";
        if (Current?.Id == item.Id)
        {
            _hasRetriedCurrentSource = false;
            if (PositionSeconds >= Math.Max(0, DurationSeconds - 0.25)) Seek(TimeSpan.Zero);
            _mediaPlayer.Play();
            StatusText = "正在播放";
            _logger.LogInformation("Playback resumed for track {TrackId} from {Source}", item.Id, item.SourceLabel);
            return Task.CompletedTask;
        }

        Prepare(item, replaceQueue);
        _mediaPlayer.Play();
        StatusText = "正在播放";
        _logger.LogInformation("Playback started for track {TrackId} from {Source}", item.Id, item.SourceLabel);
        return Task.CompletedTask;
    }

    public async Task<PlaybackOperationResult> PlaySearchResultAsync(
        SearchResultItem item,
        bool replaceQueue = true,
        CancellationToken cancellationToken = default)
    {
        var prepared = await ResolveSearchResultAsync(item, cancellationToken);
        if (!prepared.IsSuccess || prepared.Item is null) return prepared;
        await PlayAsync(prepared.Item, replaceQueue);
        return prepared;
    }

    public async Task<PlaybackOperationResult> QueueSearchResultAsync(
        SearchResultItem item,
        bool playNext = false,
        CancellationToken cancellationToken = default)
    {
        var prepared = await ResolveSearchResultAsync(item, cancellationToken);
        if (!prepared.IsSuccess || prepared.Item is null) return prepared;
        if (playNext) AddNextToQueue(prepared.Item); else AddToQueue(prepared.Item);
        return prepared;
    }

    public void Prepare(PlaybackItem item, bool replaceQueue = true)
    {
        if (replaceQueue) Queue.Clear();
        if (!Queue.Any(x => x.Id == item.Id)) Queue.Add(item);

        Current = item;
        var generation = ++_sourceGeneration;
        _hasRetriedCurrentSource = false;
        IsFavorite = false;
        PositionSeconds = 0;
        DurationSeconds = Math.Max(1, item.Duration.TotalSeconds);
        RaiseCurrentProperties();

        _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(item.SourceUri));
        UpdateSystemMediaControls(item);
        _ = RefreshFavoriteStateAsync(item, generation);
    }

    public void TogglePlayPause()
    {
        if (Current is null)
        {
            StatusText = "请先选择歌曲";
            return;
        }
        if (IsPlaying) _mediaPlayer.Pause(); else _mediaPlayer.Play();
        StatusText = IsPlaying ? "已暂停" : "正在播放";
    }

    public void Previous()
    {
        if (Current is null || Queue.Count == 0)
        {
            StatusText = "队列为空，无法播放上一首";
            return;
        }
        if (PositionSeconds > 5) { Seek(TimeSpan.Zero); return; }
        var index = Queue.IndexOf(Current);
        var previous = index <= 0 ? Queue[^1] : Queue[index - 1];
        _ = PlayAsync(previous);
    }

    public void Next()
    {
        if (Current is null || Queue.Count == 0)
        {
            StatusText = "队列为空，无法播放下一首";
            return;
        }
        var index = Queue.IndexOf(Current);
        PlaybackItem next;
        if (IsShuffleEnabled && Queue.Count > 1)
        {
            var candidates = Queue.Where(x => x.Id != Current.Id).ToArray();
            next = candidates[Random.Shared.Next(candidates.Length)];
        }
        else
        {
            next = Queue[(index + 1) % Queue.Count];
        }
        _ = PlayAsync(next);
    }

    public void Seek(TimeSpan position)
    {
        if (Current is null) return;
        var bounded = Math.Clamp(position.TotalSeconds, 0, Math.Max(0, DurationSeconds));
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(bounded);
        PositionSeconds = bounded;
        RaiseTimelineProperties();
    }

    public void BeginSeek() => _isSeeking = true;
    public void EndSeek(TimeSpan position) { Seek(position); _isSeeking = false; }

    public void SetVolume(double percent)
    {
        VolumePercent = Math.Clamp(percent, 0, 100);
        _mediaPlayer.Volume = VolumePercent / 100d;
    }

    public Task CycleQualityAsync(CancellationToken cancellationToken = default)
    {
        var current = Current;
        return SetQualityAsync(current is null ? AudioQuality.Standard : NextQuality(ParseQuality(current.QualityLabel)), cancellationToken);
    }

    public async Task SetQualityAsync(AudioQuality requested, CancellationToken cancellationToken = default)
    {
        var current = Current;
        if (current is null)
        {
            StatusText = "请先选择歌曲";
            return;
        }

        if (current.Platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic) || _sourceResolver is null)
        {
            StatusText = "本地文件音质由原文件决定";
            return;
        }

        StatusText = $"正在切换到 {QualityDisplayLabel(requested)}…";
        try
        {
            var resolved = await _sourceResolver.ResolveAsync(
                new PlaybackSourceRequest(
                    new MusicIdentity(current.Platform, current.NativeId ?? string.Empty),
                    requested,
                    AllowCachedSource: false,
                    ProviderMediaId: current.ProviderMediaId),
                cancellationToken);
            if (!resolved.IsSuccess || resolved.Source is null)
            {
                StatusText = resolved.SafeMessage;
                return;
            }

            var refreshed = current with
            {
                SourceUri = resolved.Source.Uri.AbsoluteUri,
                QualityLabel = QualityDisplayLabel(resolved.Source.Quality)
            };
            var index = Queue.IndexOf(current);
            if (index >= 0) Queue[index] = refreshed;
            Current = refreshed;
            RaiseCurrentProperties();
            _mediaPlayer.Source = MediaSource.CreateFromUri(resolved.Source.Uri);
            _mediaPlayer.Play();
            StatusText = $"已切换到 {refreshed.QualityLabel}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "音质切换已取消";
        }
        catch
        {
            StatusText = "音质切换失败，请检查授权或网络";
        }
    }

    public void ToggleShuffle()
    {
        IsShuffleEnabled = !IsShuffleEnabled;
        StatusText = IsShuffleEnabled ? "随机播放已开启" : "随机播放已关闭";
    }
    public void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch { PlaybackRepeatMode.Off => PlaybackRepeatMode.All, PlaybackRepeatMode.All => PlaybackRepeatMode.One, _ => PlaybackRepeatMode.Off };
        StatusText = RepeatMode switch
        {
            PlaybackRepeatMode.All => "列表循环已开启",
            PlaybackRepeatMode.One => "单曲循环已开启",
            _ => "循环播放已关闭"
        };
    }
    public void ToggleFavorite()
    {
        var item = Current;
        var generation = _sourceGeneration;
        if (_userLibrary is null || item is null || !TryCreateLibrarySnapshot(item, out var snapshot)) return;
        var desired = !IsFavorite;
        IsFavorite = desired;
        _ = PersistFavoriteAsync(snapshot, desired, item.Id, generation);
    }
    public void AddToQueue(PlaybackItem item) { if (!Queue.Any(x => x.Id == item.Id)) Queue.Add(item); }
    public void AddNextToQueue(PlaybackItem item)
    {
        if (Queue.Any(x => x.Id == item.Id)) return;
        var index = Current is null ? 0 : Queue.IndexOf(Current) + 1;
        Queue.Insert(Math.Clamp(index, 0, Queue.Count), item);
    }
    public void ClearQueue() { Queue.Clear(); Current = null; _hasRetriedCurrentSource = false; _mediaPlayer.Pause(); RaiseCurrentProperties(); }

    private async Task<PlaybackOperationResult> ResolveSearchResultAsync(SearchResultItem item, CancellationToken cancellationToken)
    {
        if (item.IsPlayable && !string.IsNullOrWhiteSpace(item.PlaybackUri))
            return PlaybackItemFactory.Create(item);

        if (item.Platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic))
            return PlaybackOperationResult.Failure(
                PlaybackRestriction.NotImplemented,
                string.IsNullOrWhiteSpace(item.RestrictionState) ? "当前来源暂不支持播放" : item.RestrictionState);
        if (_sourceResolver is null)
            return PlaybackOperationResult.Failure(PlaybackRestriction.NotImplemented, "在线播放适配暂不可用");

        try
        {
            var source = await _sourceResolver.ResolveAsync(
                new PlaybackSourceRequest(
                    new MusicIdentity(item.Platform, item.NativeId),
                    ProviderMediaId: item.ProviderMediaId), cancellationToken);
            return PlaybackItemFactory.Create(item, source);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning("Online playback source resolution failed for platform {Platform}", item.Platform.ToStableId());
            return PlaybackOperationResult.Failure(PlaybackRestriction.ProviderUnavailable, "在线播放服务暂时不可用");
        }
    }

    private void RefreshTimeline()
    {
        if (_isSeeking || Current is null) return;
        PositionSeconds = Math.Max(0, _mediaPlayer.PlaybackSession.Position.TotalSeconds);
        var natural = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (natural > 0 && !double.IsInfinity(natural)) DurationSeconds = natural;
        RaiseTimelineProperties();
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args) => Enqueue(() =>
    {
        StatusText = "正在播放";
        var natural = sender.PlaybackSession.NaturalDuration.TotalSeconds;
        if (natural > 0 && !double.IsInfinity(natural)) DurationSeconds = natural;
        RaiseTimelineProperties();
        if (Current is { } current && _recordedHistoryGeneration != _sourceGeneration)
        {
            _recordedHistoryGeneration = _sourceGeneration;
            _ = RecordSuccessfulPlaybackAsync(current, TimeSpan.Zero);
        }
    });

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args) => Enqueue(() =>
    {
        if (RepeatMode == PlaybackRepeatMode.One) { Seek(TimeSpan.Zero); sender.Play(); }
        else if (RepeatMode == PlaybackRepeatMode.All || Queue.IndexOf(Current!) < Queue.Count - 1) Next();
        else IsPlaying = false;
    });

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Enqueue(() =>
    {
        IsPlaying = false;
        StatusText = "音频无法打开，请检查授权或网络后重试";
        _logger.LogWarning("Playback failed with safe error code {ErrorCode}", args.Error.ToString());
        if (!_hasRetriedCurrentSource && TryGetOnlineIdentity(Current, out var identity))
        {
            _hasRetriedCurrentSource = true;
            _ = RetryOnlineSourceAsync(identity, Current!);
        }
    });

    private async Task RetryOnlineSourceAsync(MusicIdentity identity, PlaybackItem failedItem)
    {
        if (_sourceResolver is null) return;
        try
        {
            var result = await _sourceResolver.ResolveAsync(
                new PlaybackSourceRequest(
                    identity,
                    ParseQuality(failedItem.QualityLabel),
                    AllowCachedSource: false,
                    ProviderMediaId: failedItem.ProviderMediaId),
                CancellationToken.None);
            if (!result.IsSuccess || result.Source is null) return;
            if (Current?.Id != failedItem.Id) return;

            var refreshed = failedItem with
            {
                SourceUri = result.Source.Uri.AbsoluteUri,
                QualityLabel = QualityDisplayLabel(result.Source.Quality)
            };
            var index = Queue.IndexOf(failedItem);
            if (index >= 0) Queue[index] = refreshed;
            Current = refreshed;
            PositionSeconds = 0;
            DurationSeconds = Math.Max(1, refreshed.Duration.TotalSeconds);
            RaiseCurrentProperties();
            _mediaPlayer.Source = MediaSource.CreateFromUri(result.Source.Uri);
            UpdateSystemMediaControls(refreshed);
            _mediaPlayer.Play();
            StatusText = "正在重试播放源…";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Provider and credential details stay behind the resolver boundary.
        }
    }

    private static bool TryGetOnlineIdentity(PlaybackItem? item, out MusicIdentity identity)
    {
        identity = default;
        if (item is null) return false;
        var marker = item.Id.IndexOf(":track:", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0 || !PlatformIdExtensions.TryParseStableId(item.Id[..marker], out var platform) ||
            platform is not (PlatformId.QqMusic or PlatformId.NetEaseMusic)) return false;
        var nativeId = item.Id[(marker + ":track:".Length)..];
        if (string.IsNullOrWhiteSpace(nativeId)) return false;
        identity = new MusicIdentity(platform, nativeId);
        return true;
    }

    private async Task RefreshFavoriteStateAsync(PlaybackItem item, int generation)
    {
        if (_userLibrary is null || !TryCreateLibrarySnapshot(item, out var snapshot)) return;
        try
        {
            var isFavorite = await _userLibrary.IsFavoriteAsync(snapshot.Kind, snapshot.Platform, snapshot.NativeId);
            Enqueue(() =>
            {
                if (_sourceGeneration == generation && Current?.Id == item.Id) IsFavorite = isFavorite;
            });
        }
        catch (Exception)
        {
            _logger.LogWarning("Unable to read local favorite state for the current track");
        }
    }

    private async Task PersistFavoriteAsync(
        LibraryMediaSnapshot snapshot,
        bool desired,
        string itemId,
        int generation)
    {
        try
        {
            await _userLibrary!.SetFavoriteAsync(snapshot, desired);
        }
        catch (Exception)
        {
            _logger.LogWarning("Unable to update local favorite state for the current track");
            Enqueue(() =>
            {
                if (_sourceGeneration == generation && Current?.Id == itemId) IsFavorite = !desired;
            });
        }
    }

    private async Task RecordSuccessfulPlaybackAsync(PlaybackItem item, TimeSpan duration)
    {
        if (_userLibrary is null || !TryCreateLibrarySnapshot(item, out var snapshot)) return;
        try
        {
            await _userLibrary.RecordPlaybackAsync(snapshot, duration);
        }
        catch (Exception)
        {
            _logger.LogWarning("Unable to update local playback history for the current track");
        }
    }

    private static bool TryCreateLibrarySnapshot(PlaybackItem item, out LibraryMediaSnapshot snapshot)
    {
        snapshot = null!;
        if (item.DataOrigin == SearchDataOrigin.Preview ||
            item.Platform is PlatformId.Beans or PlatformId.KuGouMusic ||
            string.IsNullOrWhiteSpace(item.NativeId) ||
            string.IsNullOrWhiteSpace(item.Title)) return false;

        snapshot = new LibraryMediaSnapshot(
            LibraryItemKind.Track,
            item.Platform,
            item.NativeId,
            item.Title,
            item.Artist,
            item.Album,
            item.ArtworkUri,
            item.Duration > TimeSpan.Zero ? (long)item.Duration.TotalMilliseconds : null,
            item.QualityLabel,
            item.DataOrigin);
        return true;
    }

    private static AudioQuality ParseQuality(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "HI-RES" or "HIRES" => AudioQuality.HiRes,
        "无损" or "LOSSLESS" => AudioQuality.Lossless,
        "SQ" or "超高" or "VERYHIGH" => AudioQuality.VeryHigh,
        "HQ" or "高" or "HIGH" => AudioQuality.High,
        _ => AudioQuality.Standard
    };

    private static AudioQuality NextQuality(AudioQuality current) => current switch
    {
        AudioQuality.Standard => AudioQuality.High,
        AudioQuality.High => AudioQuality.VeryHigh,
        AudioQuality.VeryHigh => AudioQuality.Lossless,
        AudioQuality.Lossless => AudioQuality.HiRes,
        _ => AudioQuality.Standard
    };

    private static string QualityDisplayLabel(AudioQuality quality) => quality switch
    {
        AudioQuality.HiRes => "Hi-Res",
        AudioQuality.Lossless => "无损",
        AudioQuality.VeryHigh => "SQ",
        AudioQuality.High => "HQ",
        _ => "标准"
    };

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args) => Enqueue(() =>
    {
        IsPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
        OnPropertyChanged(nameof(PlayPauseGlyph));
    });

    private void UpdateSystemMediaControls(PlaybackItem item)
    {
        var controls = _mediaPlayer.SystemMediaTransportControls;
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsNextEnabled = Queue.Count > 1;
        controls.IsPreviousEnabled = Queue.Count > 1;

        var updater = controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = item.Title;
        updater.MusicProperties.Artist = item.Artist;
        updater.MusicProperties.AlbumTitle = item.Album;
        updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(item.ArtworkUri));
        updater.Update();
    }

    private void Enqueue(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private void RaiseCurrentProperties()
    {
        foreach (var property in new[] { nameof(Title), nameof(Artist), nameof(Album), nameof(ArtworkUri), nameof(SourceLabel), nameof(QualityLabel) }) OnPropertyChanged(property);
    }

    private void RaiseTimelineProperties()
    {
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
    }

    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _positionTimer?.Stop();
        _mediaPlayer.Dispose();
    }
}
